using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Security;

// CORS: qué páginas de OTRO origen pueden leer las respuestas de la API desde el navegador.
// No es control de acceso: la API responde igual a cualquiera que tenga un token válido (curl, Postman, otro servidor).
// Lo que CORS decide es si el NAVEGADOR deja que el JavaScript de esa página lea la respuesta.
public static class CorsSetup
{
    public static IServiceCollection AddFrontendCors(this IServiceCollection services)
    {
        services.AddOptions<CorsOriginsOptions>()
            .BindConfiguration(CorsOriginsOptions.SectionName)
            .Validate(options => options.HasOnlyOrigins(),
                "Cors:AllowedOrigins: cada valor es esquema://host:puerto, sin ruta ni \"/\" final.")
            .ValidateOnStart();

        services.AddCors();

        // Política por defecto armada desde la configuración. Sin orígenes configurados, no permite ninguno.
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<CorsOriginsOptions>>((cors, origins) => cors.AddDefaultPolicy(policy => policy
                .WithOrigins(origins.Value.AllowedOrigins)
                // Authorization y Content-Type: JSON no es un tipo "simple", así que el navegador pregunta antes
                // (preflight, OPTIONS) si puede mandarlos.
                .WithHeaders("Authorization", "Content-Type")
                .WithMethods("GET", "POST", "PUT", "DELETE")
                // Por defecto el JavaScript solo ve unos pocos headers de la respuesta; estos los necesita leer.
                .WithExposedHeaders("Location", "Retry-After")
                // El navegador recuerda la respuesta del preflight y no lo repite en cada llamada.
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
        // Sin AllowCredentials: el frontend se identifica con un token en el header, no con cookies. Mandar cookies
        // a otro origen (AllowCredentials) exige orígenes exactos y choca con SameSite; para cookies, mejor un BFF.

        return services;
    }
}
