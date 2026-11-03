using StackExchange.Redis;

namespace HelpDesk.Api.Caching;

// Dos cachés, porque guardan cosas distintas:
// - Output caching: la respuesta HTTP completa, con un atributo en el endpoint. Sin Redis: guardarla en cada instancia
//   es suficiente con una duración de pocos segundos, y evita que un Redis caído afecte a estas peticiones.
// - HybridCache: datos, desde el código que quiera usarlos. Memoria de cada instancia + Redis (compartido entre instancias),
//   y evita que varias peticiones simultáneas repitan la misma consulta.
public static class CachingSetup
{
    private const string RedisKeyPrefix = "helpdesk:";

    public static IServiceCollection AddHelpDeskCaching(this IServiceCollection services, ConfigurationOptions? redis)
    {
        services.AddOutputCache(options => options.AddPolicy(
            AgentSummaryCache.Policy,
            new SharedResponsePolicy(AgentSummaryCache.Duration, AgentSummaryCache.Tag)));

        services.AddHybridCache();

        // HybridCache usa como segundo nivel el IDistributedCache que encuentre registrado, si hay uno.
        if (redis is not null)
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.ConfigurationOptions = redis;
                options.InstanceName = RedisKeyPrefix;
            });
        }

        return services;
    }
}
