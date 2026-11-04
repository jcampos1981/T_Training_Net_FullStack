namespace HelpDesk.ServiceDefaults;

// Una imagen chiseled no trae shell ni curl: dentro del contenedor no hay con qué preguntarle a la aplicación cómo está. Por eso
// el HEALTHCHECK del Dockerfile la ejecuta otra vez con este argumento: el proceso nuevo llama a /health/ready del que ya está
// atendiendo, sale con 0 o 1, y no agrega ninguna herramienta a la imagen. Kubernetes no lo necesita: sus probes llaman por HTTP.
public static class HealthProbe
{
    private const string Argument = "--health-probe";
    private const string DefaultPort = "8080";

    // La misma variable de la que Kestrel toma el puerto en las imágenes de .NET.
    private const string PortsVariable = "ASPNETCORE_HTTP_PORTS";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static bool IsRequested(string[] args) => args.Contains(Argument);

    public static async Task<int> RunAsync()
    {
        using var http = new HttpClient { Timeout = Timeout };

        try
        {
            using HttpResponseMessage response = await http.GetAsync(ReadinessAddress(Environment.GetEnvironmentVariable(PortsVariable)));

            if (response.IsSuccessStatusCode)
            {
                return 0;
            }

            await Console.Error.WriteLineAsync($"/health/ready respondió {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"/health/ready no respondió: {exception.Message}");
        }

        return 1;
    }

    // La variable puede traer varios puertos ("8080;8081"): cualquiera sirve, y se usa el primero.
    public static Uri ReadinessAddress(string? httpPorts)
    {
        string port = httpPorts?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? DefaultPort;

        return new Uri($"http://127.0.0.1:{port}/health/ready");
    }
}
