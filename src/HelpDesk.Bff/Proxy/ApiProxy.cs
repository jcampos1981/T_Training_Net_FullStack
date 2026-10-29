using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Yarp.ReverseProxy.Transforms;

namespace HelpDesk.Bff.Proxy;

// Reenvía /api/... a HelpDesk.Api con YARP (el reverse proxy de Microsoft). La ruta y el destino están en
// appsettings ("ReverseProxy"). Para el navegador, la API y el BFF son el mismo origen: no hace falta CORS.
public static class ApiProxy
{
    private const string AntiforgeryHeader = "X-CSRF";

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

    // La ruta exige sesión ("AuthorizationPolicy": "default" en appsettings) y además el header X-CSRF.
    public static IEndpointRouteBuilder MapApiProxy(this IEndpointRouteBuilder app)
    {
        app.MapReverseProxy(proxy => proxy.Use(RequireAntiforgeryHeaderAsync));
        return app;
    }

    // CSRF: otra página puede hacer que el navegador mande la cookie (un <form> que se envía solo). SameSite=Strict
    // lo frena si la página es de otro SITIO, pero "sitio" ignora el puerto: cualquier app en localhost:xxxx, o en
    // otro subdominio de la empresa, cuenta como el mismo sitio. Un header propio, en cambio, solo se puede agregar
    // con fetch, y desde otro ORIGEN eso exige permiso CORS, que el BFF no da a nadie.
    private static async Task RequireAntiforgeryHeaderAsync(HttpContext context, Func<Task> next)
    {
        if (context.Request.Headers[AntiforgeryHeader] != "1")
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
}
