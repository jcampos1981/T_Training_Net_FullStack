using HelpDesk.Admin.Authentication;
using Microsoft.Extensions.Options;

namespace HelpDesk.Admin.Security;

public static class SecurityHeaders
{
    // Defensa en profundidad contra XSS: el navegador solo ejecuta scripts de este origen y solo se conecta a él.
    // - style-src 'unsafe-inline': MudBlazor escribe estilos en línea (colores del tema, posición de los menús). Es una
    //   concesión de la librería. El portal React, sin esa dependencia, no la necesita.
    // - form-action incluye a Keycloak: el POST de "Cerrar sesión" termina redirigiendo allá, y Chrome también
    //   revisa los redirects de un formulario.
    public static IApplicationBuilder UseSecurityHeaders(this WebApplication app)
    {
        Uri authority = new(app.Services.GetRequiredService<IOptions<KeycloakClientOptions>>().Value.Authority);
        string keycloakOrigin = authority.GetLeftPart(UriPartial.Authority);

        string policy =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
            $"connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self' {keycloakOrigin}; frame-ancestors 'none'";

        return app.Use((context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy = policy;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return next(context);
        });
    }
}
