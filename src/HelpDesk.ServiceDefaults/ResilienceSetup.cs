using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace HelpDesk.ServiceDefaults;

public static class ResilienceSetup
{
    // Reintentos con espera creciente, tiempos límite y circuit breaker, para una falla de segundos en la llamada a otro servicio.
    //
    // Solo se reintenta lo que se puede repetir sin efectos: GET, HEAD... Un POST que recibió un 503 quizá sí se procesó, y
    // repetirlo crearía un ticket duplicado. Por defecto, el handler estándar reintenta también esos.
    public static IHttpClientBuilder AddHelpDeskResilience(this IHttpClientBuilder builder, TimeSpan? retryDelay = null)
    {
        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.DisableForUnsafeHttpMethods();
            options.Retry.Delay = retryDelay ?? options.Retry.Delay;

            // Más que los 10 segundos por defecto: reasignar los tickets de un agente puede tocar miles de filas.
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60); // debe ser al menos el doble del tiempo por intento
        });

        return builder;
    }
}
