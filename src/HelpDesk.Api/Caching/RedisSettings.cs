using StackExchange.Redis;

namespace HelpDesk.Api.Caching;

public static class RedisSettings
{
    private const string ConnectionName = "Redis";
    private const int TimeoutMilliseconds = 1000;

    // null = sin Redis configurado: la API funciona igual, con una sola instancia (caché en memoria y avisos locales).
    public static ConfigurationOptions? From(IConfiguration configuration)
    {
        if (configuration.GetConnectionString(ConnectionName) is not { } connection)
        {
            return null;
        }

        ConfigurationOptions options = ConfigurationOptions.Parse(connection);

        // Redis es una mejora, no una dependencia: la API tiene que arrancar y responder aunque esté caído.
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = TimeoutMilliseconds;
        options.AsyncTimeout = TimeoutMilliseconds;
        options.SyncTimeout = TimeoutMilliseconds;

        // Sin conexión, falla al instante. Por defecto los comandos esperan en una cola a que Redis vuelva: cada petición
        // que usa la caché o manda un aviso quedaría esperando hasta el tiempo límite.
        options.BacklogPolicy = BacklogPolicy.FailFast;

        return options;
    }
}
