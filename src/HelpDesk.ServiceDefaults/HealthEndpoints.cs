using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace HelpDesk.ServiceDefaults;

// Dos preguntas distintas, que un orquestador como Kubernetes hace por separado (Fase 10):
// - /health/live: ¿el proceso responde? Si no, hay que reiniciarlo. No mira las dependencias: reiniciar no arregla una base
//   caída, y reiniciar todas las instancias a la vez solo empeoraría el problema.
// - /health/ready: ¿puede atender tráfico? Si no, se le deja de enviar, sin reiniciarlo.
//
// Están abiertos, porque el orquestador no inicia sesión, y por eso solo responden Healthy, Degraded o Unhealthy, sin
// detalles. Van sin límite de peticiones: un 429 haría creer que la instancia está caída.
public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous()
            .DisableRateLimiting();

        // Degraded responde 200: la instancia sigue atendiendo. Solo Unhealthy responde 503.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) })
            .AllowAnonymous()
            .DisableRateLimiting();

        return app;
    }
}
