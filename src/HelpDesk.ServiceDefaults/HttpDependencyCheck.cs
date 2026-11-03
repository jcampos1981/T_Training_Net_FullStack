using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HelpDesk.ServiceDefaults;

// Revisa que otro servicio responda por HTTP.
public sealed class HttpDependencyCheck(IHttpClientFactory clients, Uri url) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using HttpClient http = clients.CreateClient();
        using HttpResponseMessage response = await http.GetAsync(url, cancellationToken);

        return response.IsSuccessStatusCode
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, $"Respondió {(int)response.StatusCode}.");
    }
}

public static class HttpDependencyCheckExtensions
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    // Degraded y no Unhealthy: si otro servicio se cae, sacar también a este del tráfico no arregla nada, y convierte la
    // falla de uno en la caída de todos.
    public static IHealthChecksBuilder AddHttpDependency(this IHealthChecksBuilder builder, string name, Uri url)
    {
        builder.Services.AddHttpClient();

        return builder.AddTypeActivatedCheck<HttpDependencyCheck>(
            name, HealthStatus.Degraded, [HealthEndpoints.ReadyTag], Timeout, url);
    }
}
