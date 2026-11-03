using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
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
        app.MapGet("/api/sla/alerts", ListSlaAlerts)
            .WithTags("SLA")
            .WithName(nameof(ListSlaAlerts))
            .WithSummary("Alertas de SLA de tickets abiertos, las más recientes primero (keyset: ?after=&pageSize=). Un agente solo ve las suyas.")
            .RequireAuthorization(HelpDeskPolicies.Staff)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<Results<Ok<CursorPage<SlaAlertResponse>>, ForbidHttpResult>> ListSlaAlerts(
        [AsParameters] PageRequest page,
        ClaimsPrincipal user,
        SlaAlertQueries queries,
        CancellationToken cancellationToken,
        SlaState? level = null)
    {
        // La misma regla que GET /api/tickets: un agente solo ve lo suyo.
        var everything = new TicketFilter(Status: null, Priority: null, AssignedTo: null);
        if (TicketAuthorizationHandler.ScopeListFilter(user, everything) is not { } allowed)
        {
            return TypedResults.Forbid();
        }

        IReadOnlyList<SlaAlertResponse> alerts = await queries.ListAsync(
            allowed.AssignedTo, level, page.After > 0 ? page.After : null, page.Take(), cancellationToken);

        return TypedResults.Ok(CursorPage.Create(alerts, page, alert => alert, alert => alert.Id));
    }
}
