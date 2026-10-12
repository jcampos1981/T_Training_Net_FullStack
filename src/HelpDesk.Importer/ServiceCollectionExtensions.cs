using HelpDesk.Importer.Common;
using HelpDesk.Domain;
using HelpDesk.Importer.Import;
using HelpDesk.Importer.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HelpDesk.Importer;

// Composition root: el único lugar que sabe qué implementación concreta corresponde a cada dependencia.
public static class ServiceCollectionExtensions
{
    private const string ImportSection = "Import";
    private const string ReportSection = "Report";
    private const string SlaSection = "Sla";

    public static IServiceCollection AddHelpDeskImporter(this IServiceCollection services)
    {
        services.AddOptions<ImportOptions>()
            .BindConfiguration(ImportSection)
            .Validate(options => !string.IsNullOrWhiteSpace(options.Folder), "Import:Folder es obligatorio.")
            .ValidateOnStart();

        services.AddOptions<ReportOptions>()
            .BindConfiguration(ReportSection)
            .Validate(options => options.Team.Count > 0, "Report:Team debe tener al menos un agente.")
            .Validate(options => options.OldestOpenCount > 0, "Report:OldestOpenCount debe ser mayor que cero.")
            .ValidateOnStart();

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

        services.AddSingleton<TimeProvider>(provider =>
            provider.GetRequiredService<IOptions<ReportOptions>>().Value.AsOf is { } asOf
                ? new FixedTimeProvider(asOf)
                : TimeProvider.System);

        services.AddSingleton<ISlaPolicy>(provider => new StandardSlaPolicy(
            provider.GetRequiredService<IOptions<SlaOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton<TicketImporter>();
        services.AddSingleton<ConsoleReportWriter>();
        services.AddSingleton<ImportApp>();

        return services;
    }
}
