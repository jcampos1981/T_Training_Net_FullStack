namespace HelpDesk.Api.Features.Agents;

// Los Controllers se descubren solos (MapControllers); aquí solo se registran sus servicios.
public static class AgentsFeature
{
    public static IServiceCollection AddAgentsFeature(this IServiceCollection services)
    {
        services.AddScoped<ReassignTicketsHandler>();
        return services;
    }
}
