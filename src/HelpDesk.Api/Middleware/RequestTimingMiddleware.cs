using System.Diagnostics;

namespace HelpDesk.Api.Middleware;

// Middleware "por convención": recibe el siguiente eslabón (RequestDelegate) y expone InvokeAsync.
// Equivale a un app.use((req, res, next) => ...) de Express.
public sealed class RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            await next(context); // Lo que sigue a esta línea corre de regreso, con la respuesta ya armada.
            LogRequest(context, startedAt, LogLevel.Information, context.Response.StatusCode.ToString());
        }
        catch (Exception exception)
        {
            // Una excepción de un middleware posterior también se registra; luego sigue su camino.
            LogRequest(context, startedAt, LogLevel.Warning, exception.GetType().Name);
            throw;
        }
    }

    private void LogRequest(HttpContext context, long startedAt, LogLevel level, string outcome) =>
        logger.Log(
            level,
            "{Method} {Path}{Query} → {Outcome} en {ElapsedMs:0.0} ms",
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString,
            outcome,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
}
