using HelpDesk.Domain;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Features.Sla;

// Plazos de resolución: la regla está en el dominio (ISlaPolicy), y aquí se vigila y se consulta.
public static class SlaFeature
{
    private const string SlaSection = "Sla";

    public static IServiceCollection AddSlaFeature(this IServiceCollection services)
    {
        services.AddOptions<SlaOptions>()
            .BindConfiguration(SlaSection)
            .Validate(
                options => Enum.GetValues<TicketPriority>().All(options.ResolutionHours.ContainsKey),
                "Sla:ResolutionHours debe definir horas para todas las prioridades.")
            .Validate(
                options => options.ResolutionHours.Values.All(hours => hours > 0),
                "Sla:ResolutionHours solo admite horas mayores que cero.")
            .Validate(
                options => options.AtRiskThreshold is > 0 and < 1,
                "Sla:AtRiskThreshold debe estar entre 0 y 1.")
            .ValidateOnStart();

        services.AddOptions<SlaMonitorOptions>()
            .BindConfiguration(SlaMonitorOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ISlaPolicy>(provider => new StandardSlaPolicy(
            provider.GetRequiredService<IOptions<SlaOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));

        services.AddScoped<SlaAlertScanner>();
        services.AddScoped<SlaAlertQueries>();

        // También como singleton con su propio nombre: el health check le pregunta cuándo terminó su última vuelta.
        services.AddSingleton<SlaMonitor>();
        services.AddHostedService(provider => provider.GetRequiredService<SlaMonitor>());

        return services;
    }

    public static IEndpointRouteBuilder MapSlaFeature(this IEndpointRouteBuilder app)
    {
        ListSlaAlerts.Map(app);

        return app;
    }
}
