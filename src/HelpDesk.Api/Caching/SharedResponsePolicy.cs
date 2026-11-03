using Microsoft.AspNetCore.OutputCaching;

namespace HelpDesk.Api.Caching;

// La política por defecto de output caching no guarda nada si la petición trae Authorization, porque la respuesta podría
// ser de ese usuario. Esta sí lo hace, y solo es segura si se cumplen dos condiciones:
// - la respuesta es igual para todos los que pueden verla (no depende de quién pregunta);
// - UseOutputCache va DESPUÉS de UseAuthorization: cada petición se autoriza antes de recibir lo guardado.
public sealed class SharedResponsePolicy(TimeSpan duration, string tag) : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;
        context.AllowLocking = true; // si varias peticiones llegan a la vez sin nada guardado, solo una llama a la base
        context.ResponseExpirationTimeSpan = duration;
        context.CacheVaryByRules.QueryKeys = "*";
        context.Tags.Add(tag);

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    // Solo se guardan las respuestas buenas: un error pasajero no debe repetirse durante toda la duración.
    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        context.AllowCacheStorage = context.HttpContext.Response.StatusCode == StatusCodes.Status200OK;

        return ValueTask.CompletedTask;
    }
}
