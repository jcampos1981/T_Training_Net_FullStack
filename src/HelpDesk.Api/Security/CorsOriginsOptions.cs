namespace HelpDesk.Api.Security;

// Los orígenes (esquema://host:puerto) de los frontends que pueden llamar a la API desde OTRO origen con un token.
// Un frontend servido por el BFF no necesita estar aquí: para el navegador, el BFF y su página son el mismo origen.
public sealed class CorsOriginsOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];

    // El navegador manda "Origin: http://localhost:5173", sin "/" final ni ruta. "http://localhost:5173/" en la
    // configuración nunca coincidiría, y el error en el navegador no dice por qué.
    public bool HasOnlyOrigins() => AllowedOrigins.All(IsOrigin);

    private static bool IsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https"
        && value == uri.GetLeftPart(UriPartial.Authority);
}
