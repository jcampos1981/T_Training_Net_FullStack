using HelpDesk.Api.Background;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Sla;
using HelpDesk.Api.Outbox;
using HelpDesk.ServiceDefaults;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace HelpDesk.Api.Health;

public static class HealthSetup
{
    // Un orquestador no puede esperar mucho una respuesta: si tarda, da la instancia por caída.
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

    public static IServiceCollection AddApiHealthChecks(this IServiceCollection services, ConfigurationOptions? redis, IConfiguration configuration)
    {
        IHealthChecksBuilder checks = services.AddHealthChecks()
            // Sin base no hay nada que atender: la instancia sale del tráfico.
            .AddCheck<DatabaseHealthCheck>("database", HealthStatus.Unhealthy, [HealthEndpoints.ReadyTag], CheckTimeout)
            // Los tickets se siguen atendiendo, pero nadie vigila los plazos: hay que enterarse, sin sacarla del tráfico.
            .AddCheck<PeriodicJobHealthCheck<SlaMonitor>>("sla-monitor", HealthStatus.Degraded, [HealthEndpoints.ReadyTag], CheckTimeout);

        // Sin Redis se pierden los avisos en vivo y la caché compartida. La API sigue atendiendo.
        if (redis is not null)
        {
            checks.AddCheck<RedisHealthCheck>("redis", HealthStatus.Degraded, [HealthEndpoints.ReadyTag], CheckTimeout);
        }

        // Sin RabbitMQ los eventos esperan en la tabla, y no se pierde ninguno. La API sigue atendiendo.
        if (OutboxSetup.IsEnabled(configuration))
        {
            checks.AddCheck<PeriodicJobHealthCheck<OutboxPublisher>>("outbox", HealthStatus.Degraded, [HealthEndpoints.ReadyTag], CheckTimeout);
        }

        return services;
    }

    public static IEndpointRouteBuilder MapApiHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthEndpoints();

        // Lo mismo que /health/ready, con el detalle de cada revisión. Solo para supervisión: los abiertos no dicen qué falló.
        app.MapHealthChecks("/health/details", new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains(HealthEndpoints.ReadyTag),
                ResponseWriter = HealthDetailsWriter.WriteAsync
            })
            .RequireAuthorization(HelpDeskPolicies.Supervision);

        return app;
    }
}
