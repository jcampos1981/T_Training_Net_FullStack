using HelpDesk.ServiceDefaults;

namespace HelpDesk.Admin.Health;

public static class AdminHealthChecks
{
    private const string ApiBaseUrl = "HelpDeskApi:BaseUrl";

    // Si falta una dirección, no se registra su revisión: arrancar ya falla con un mensaje claro por esa configuración.
    public static IServiceCollection AddAdminHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        IHealthChecksBuilder checks = services.AddHealthChecks();

        if (configuration[ApiBaseUrl] is { } api)
        {
            checks.AddHttpDependency("api", new Uri(new Uri(api), "health/live"));
        }

        if (KeycloakDiscovery.DocumentAddress(configuration) is { } keycloak)
        {
            checks.AddHttpDependency("keycloak", keycloak);
        }

        return services;
    }
}
