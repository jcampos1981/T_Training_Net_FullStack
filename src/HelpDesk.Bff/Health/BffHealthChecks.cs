using HelpDesk.ServiceDefaults;

namespace HelpDesk.Bff.Health;

public static class BffHealthChecks
{
    private const string ApiAddress = "ReverseProxy:Clusters:helpdesk-api:Destinations:api:Address";
    private const string KeycloakAuthority = "Keycloak:Authority";

    // Si falta una dirección, no se registra su revisión: arrancar ya falla con un mensaje claro por esa configuración.
    public static IServiceCollection AddBffHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        IHealthChecksBuilder checks = services.AddHealthChecks();

        if (configuration[ApiAddress] is { } api)
        {
            checks.AddHttpDependency("api", new Uri(new Uri(api), "health/live"));
        }

        if (configuration[KeycloakAuthority] is { } authority)
        {
            checks.AddHttpDependency("keycloak", new Uri($"{authority.TrimEnd('/')}/.well-known/openid-configuration"));
        }

        return services;
    }
}
