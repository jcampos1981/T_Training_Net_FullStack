using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Api.Features.Dashboard;

public static class DashboardFeature
{
    public static IServiceCollection AddDashboardFeature(this IServiceCollection services)
    {
        services.AddScoped<DashboardQueries>();
        return services;
    }

    public static IEndpointRouteBuilder MapDashboardFeature(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard", GetDashboard)
            .WithTags("Dashboard")
            .WithName(nameof(GetDashboard))
            .WithSummary("Indicadores: tickets por estado, carga abierta por agente y comentarios de la última semana.");

        return app;
    }

    private static async Task<Ok<DashboardResponse>> GetDashboard(
        DashboardQueries queries,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await queries.GetAsync(clock.GetUtcNow(), cancellationToken));
}
