using Microsoft.AspNetCore.StaticFiles;

namespace HelpDesk.Bff.Frontend;

// El BFF sirve el portal React compilado (npm run build en src/HelpDesk.Portal deja los archivos en wwwroot). Así la
// app, /bff y /api son el mismo origen: no hace falta CORS y la cookie de sesión viaja sola.
public static class FrontendHosting
{
    // Defensa en profundidad contra XSS. La cookie HttpOnly impide que un script inyectado ROBE la sesión, pero no que
    // la USE: desde la página podría llamar a /api como el usuario. Con esta política el navegador solo ejecuta
    // scripts y estilos que vienen de este origen (nada inline, nada de otros dominios), solo se conecta a este origen,
    // y no deja meter la página en un <iframe> de otro sitio (clickjacking).
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private static readonly StaticFileOptions Files = new() { OnPrepareResponse = SetCacheHeaders };

    public static IApplicationBuilder UseFrontendSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
            // El navegador respeta el Content-Type y no "adivina": un archivo de texto no se ejecuta como script.
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return next(context);
        });

    public static IApplicationBuilder UseFrontendFiles(this IApplicationBuilder app) => app.UseStaticFiles(Files);

    // Cualquier ruta que no sea un archivo, /api ni /bff es una pantalla de React (/tickets/7): se responde index.html
    // y React Router decide qué mostrar. Sin esto, recargar el navegador en /tickets/7 daría 404.
    public static IEndpointRouteBuilder MapFrontendFallback(this IEndpointRouteBuilder app)
    {
        app.MapFallbackToFile("index.html", Files);
        return app;
    }

    // Vite pone un hash en el nombre de cada archivo de /assets (index-a1b2c3.js): si el contenido cambia, cambia el
    // nombre. Por eso se guardan en caché un año sin volver a preguntar. index.html no: se revalida siempre, porque es
    // el que apunta a los nombres nuevos después de cada despliegue.
    private static void SetCacheHeaders(StaticFileResponseContext context)
    {
        bool fingerprinted = context.Context.Request.Path.StartsWithSegments("/assets");
        context.Context.Response.Headers.CacheControl = fingerprinted ? "public, max-age=31536000, immutable" : "no-cache";
    }
}
