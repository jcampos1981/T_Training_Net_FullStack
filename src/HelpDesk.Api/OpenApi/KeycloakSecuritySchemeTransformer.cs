using HelpDesk.Api.Features.Accounts.Keycloak;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace HelpDesk.Api.OpenApi;

// Agrega al documento OpenAPI el esquema OAuth2 de Keycloak (Authorization Code). Con esto Scalar ofrece iniciar
// sesión en Keycloak, además de pegar un token a mano (esquema "Bearer"). Sin Keycloak configurado, no agrega nada.
internal sealed class KeycloakSecuritySchemeTransformer(IOptions<KeycloakOptions> options) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        KeycloakOptions keycloak = options.Value;
        if (!keycloak.IsEnabled)
        {
            return Task.CompletedTask;
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[KeycloakAuthentication.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "Inicia sesión en Keycloak (realm helpdesk) y usa el access token que devuelve.",
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = keycloak.AuthorizationEndpoint,
                    TokenUrl = keycloak.TokenEndpoint,
                    Scopes = new Dictionary<string, string>
                    {
                        ["openid"] = "Identificar al usuario (OpenID Connect)",
                        ["profile"] = "Nombre de usuario",
                        ["email"] = "Email"
                    }
                }
            }
        };

        // Cada elemento de "security" es una alternativa: basta con cumplir una (Bearer O Keycloak).
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(KeycloakAuthentication.SchemeName, document)] = []
        });

        return Task.CompletedTask;
    }
}
