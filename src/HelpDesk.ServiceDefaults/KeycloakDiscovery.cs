using Microsoft.Extensions.Configuration;

namespace HelpDesk.ServiceDefaults;

// Keycloak tiene dos direcciones cuando la aplicación corre en contenedores:
// - Keycloak:Authority es la pública: la que abre el navegador para iniciar sesión y la que viene en el "iss" de los tokens.
// - Keycloak:MetadataAddress es de donde el servidor pide el discovery document. Dentro de la red de contenedores, "localhost" es
//   el propio contenedor y esa dirección no llega a Keycloak.
// Sin MetadataAddress, las dos son la misma, como en desarrollo.
public static class KeycloakDiscovery
{
    private const string Authority = "Keycloak:Authority";
    private const string MetadataAddress = "Keycloak:MetadataAddress";

    // null si Keycloak no está configurado.
    public static Uri? DocumentAddress(IConfiguration configuration)
    {
        if (configuration[MetadataAddress] is { Length: > 0 } metadata)
        {
            return new Uri(metadata);
        }

        return configuration[Authority] is { Length: > 0 } authority
            ? new Uri($"{authority.TrimEnd('/')}/.well-known/openid-configuration")
            : null;
    }
}
