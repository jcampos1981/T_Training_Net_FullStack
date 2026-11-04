namespace HelpDesk.Api.Features.Agents;

// Los Controllers se descubren solos (MapControllers), y los handlers también (AddMediation). Aquí, lo demás.
public static class AgentsFeature
{
    public static IServiceCollection AddAgentsFeature(this IServiceCollection services)
    {
        services.AddScoped<AgentQueries>();
        return services;
    }
}
