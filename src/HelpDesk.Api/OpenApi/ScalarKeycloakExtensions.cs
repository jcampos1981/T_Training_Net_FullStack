using HelpDesk.Api.Features.Accounts.Keycloak;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

namespace HelpDesk.Api.OpenApi;

internal static class ScalarKeycloakExtensions
{
    // Scalar inicia sesión igual que lo hará el frontend en la Fase 6: Authorization Code + PKCE con un client
    // público. Abre el login de Keycloak en una ventana, recibe un código de un solo uso y lo cambia por los tokens.
    // PKCE: Scalar inventa un secreto por cada login (code_verifier) y al pedir el código manda solo su hash
    // (code_challenge). Quien robe el código en la redirección no puede canjearlo sin el secreto original.
    public static ScalarOptions AddKeycloakLogin(this ScalarOptions options, IServiceProvider services)
    {
        KeycloakOptions keycloak = services.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        if (!keycloak.IsEnabled || keycloak.ScalarClientId is not { } clientId)
        {
            return options;
        }

        return options
            .AddPreferredSecuritySchemes([KeycloakAuthentication.SchemeName])
            .AddAuthorizationCodeFlow(KeycloakAuthentication.SchemeName, flow => flow
                .WithClientId(clientId)
                .WithPkce(Pkce.Sha256)
                .WithSelectedScopes(["openid", "profile", "email"]));
    }
}
