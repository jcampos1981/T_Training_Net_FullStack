using HelpDesk.Api.Features.Accounts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;

namespace HelpDesk.Api.Features.Dashboard;

public static class DashboardFeature
{
    public const string CacheKey = "dashboard";

    // Cada carga cuesta unas 30 mil lecturas y 400 ms de CPU, y cada aviso en vivo la dispara en todos los dashboards
    // abiertos. Con esto, como máximo una consulta cada 4 s, sin importar cuántos haya.
    // 4 s en Redis + 1 s en la memoria de cada instancia = hasta 5 s de atraso. El panel espera más que eso antes de
    // recargar (Dashboard.razor): así, la recarga que dispara un aviso siempre trae los datos nuevos.
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(4),
        LocalCacheExpiration = TimeSpan.FromSeconds(1)
    };

    public static IServiceCollection AddDashboardFeature(this IServiceCollection services)
    {
        services.AddScoped<DashboardQueries>();
        return services;
    }

    public static IEndpointRouteBuilder MapDashboardFeature(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard", GetDashboard)
            .RequireAuthorization(HelpDeskPolicies.Supervision) // la carga de TODO el equipo: solo supervisión
            .WithTags("Dashboard")
            .WithName(nameof(GetDashboard))
            .WithSummary("Indicadores: tickets por estado, carga abierta por agente y comentarios de la última semana.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<Ok<DashboardResponse>> GetDashboard(
        HybridCache cache,
        IServiceScopeFactory scopes,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // Si llegan 50 peticiones a la vez sin nada guardado, solo una ejecuta la consulta y las demás esperan su resultado.
        DashboardResponse dashboard = await cache.GetOrCreateAsync(
            CacheKey,
            (scopes, clock),
            static (state, cancellation) => ComputeAsync(state.scopes, state.clock, cancellation),
            CacheOptions,
            cancellationToken: cancellationToken);

        return TypedResults.Ok(dashboard);
    }

    // Un scope propio y no el de la petición: esta consulta la comparten varias peticiones. Si la primera se cancela (el
    // usuario cerró la página), su scope se libera con todo y su DbContext, y las demás siguen esperando este resultado.
    private static async ValueTask<DashboardResponse> ComputeAsync(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        DashboardQueries queries = scope.ServiceProvider.GetRequiredService<DashboardQueries>();

        return await queries.GetAsync(clock.GetUtcNow(), cancellationToken);
    }
}
