using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Yarp.ReverseProxy.Transforms;

namespace HelpDesk.Bff.Proxy;

// Reenvía /api/... y /hubs/... a HelpDesk.Api con YARP (el reverse proxy de Microsoft). Las rutas y el destino están en
// appsettings ("ReverseProxy"). Para el navegador, la API y el BFF son el mismo origen: no hace falta CORS.
public static class ApiProxy
{
    private const string AntiforgeryHeader = "X-CSRF";
    private const string RetryAfterSeconds = "15";

    public static IServiceCollection AddApiProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddReverseProxy()
            .LoadFromConfig(configuration.GetSection("ReverseProxy"))
            .AddTransforms(context =>
            {
                // La cookie del BFF no sale de aquí: la API no la necesita y no tiene por qué verla.
                context.AddRequestHeaderRemove("Cookie");

                // El access token guardado en la sesión va como "Authorization: Bearer ..." hacia la API.
                // Si estaba por vencer, BffCookieEvents ya lo renovó al leer la cookie.
                context.AddRequestTransform(async transform =>
                {
                    string? accessToken = await transform.HttpContext.GetTokenAsync("access_token");
                    if (accessToken is not null)
                    {
                        transform.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    }
                });
            });

        return services;
    }

    // La ruta exige sesión ("AuthorizationPolicy": "default" en appsettings), el header X-CSRF y un token vigente.
    public static IEndpointRouteBuilder MapApiProxy(this IEndpointRouteBuilder app)
    {
        app.MapReverseProxy(proxy =>
        {
            proxy.Use(RequireAntiforgeryHeaderAsync);
            proxy.Use(RequireLiveAccessTokenAsync);
        });
        return app;
    }

    // CSRF: otra página puede hacer que el navegador mande la cookie (un <form> que se envía solo). SameSite=Strict
    // lo frena si la página es de otro SITIO, pero "sitio" ignora el puerto: cualquier app en localhost:xxxx, o en
    // otro subdominio de la empresa, cuenta como el mismo sitio. Un header propio, en cambio, solo se puede agregar
    // con fetch, y desde otro ORIGEN eso exige permiso CORS, que el BFF no da a nadie.
    //
    // Un WebSocket es el caso contrario: el navegador no deja agregarle headers, y además no pasa por CORS. A cambio lleva
    // Origin, que escribe el navegador y JavaScript no puede cambiar. Sin revisarlo, cualquier página del mismo sitio
    // podría abrir el hub con la cookie del usuario y escuchar sus avisos (Cross-Site WebSocket Hijacking).
    private static async Task RequireAntiforgeryHeaderAsync(HttpContext context, Func<Task> next)
    {
        if (context.Request.Headers[AntiforgeryHeader] != "1" && !ComesFromThisOrigin(context.Request))
        {
            await TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Falta el header X-CSRF",
                detail: "Las llamadas a /api desde el frontend deben llevar el header X-CSRF: 1.")
                .ExecuteAsync(context);
            return;
        }

        await next();
    }

    // Se compara el host y el puerto, no el esquema: detrás de un proxy puede variar (Vite reenvía el WebSocket con
    // X-Forwarded-Proto: ws, y Origin dice http). En un mismo host y puerto no hay otra aplicación que pueda atacar.
    private static bool ComesFromThisOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin, UriKind.Absolute, out Uri? origin)
        && string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);

    // Si el token ya venció al llegar aquí, BffCookieEvents no pudo renovarlo: Keycloak no responde. Con ese token la
    // API respondería 401, y el frontend creería que la sesión terminó. Es una falla pasajera, y se dice como tal: 503.
    private static async Task RequireLiveAccessTokenAsync(HttpContext context, Func<Task> next)
    {
        string? expiresAt = await context.GetTokenAsync("expires_at");
        TimeProvider clock = context.RequestServices.GetRequiredService<TimeProvider>();

        if (expiresAt is not null && DateTimeOffset.Parse(expiresAt, CultureInfo.InvariantCulture) <= clock.GetUtcNow())
        {
            context.Response.Headers.RetryAfter = RetryAfterSeconds;
            await TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No se pudo renovar la sesión",
                detail: "El servicio de inicio de sesión no responde. Tu sesión sigue activa: vuelve a intentar en unos segundos.")
                .ExecuteAsync(context);
            return;
        }

        await next();
    }
}
