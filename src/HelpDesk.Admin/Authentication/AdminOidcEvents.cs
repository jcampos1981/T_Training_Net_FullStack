using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace HelpDesk.Admin.Authentication;

// Los dos momentos del login y el logout en que el panel toca los tokens.
public sealed class AdminOidcEvents(SessionTokenStore store, TimeProvider clock) : OpenIdConnectEvents
{
    // Keycloak acaba de entregar los tokens: quedan en el servidor. En la cookie solo van los claims (SaveTokens = false).
    public override Task TokenValidated(TokenValidatedContext context)
    {
        OpenIdConnectMessage tokens = context.TokenEndpointResponse
            ?? throw new InvalidOperationException("El flujo Authorization Code siempre pasa por el token endpoint.");
        string sessionId = context.Principal?.FindFirstValue(AdminAuthentication.SessionIdClaim)
            ?? throw new InvalidOperationException("El ID token de Keycloak no trae el claim sid.");

        store.Save(sessionId, new SessionTokens(
            tokens.AccessToken,
            tokens.RefreshToken,
            tokens.IdToken,
            AccessTokenRefresher.ExpirationOf(tokens, clock)));

        return Task.CompletedTask;
    }

    // Logout: Keycloak recibe el ID token como prueba de quién sale (sin él pediría confirmación al usuario), y los
    // tokens se borran del servidor. Las otras pestañas lo notarán en su próxima llamada a la API.
    public override Task RedirectToIdentityProviderForSignOut(RedirectContext context)
    {
        if (context.HttpContext.User.FindFirstValue(AdminAuthentication.SessionIdClaim) is { } sessionId)
        {
            context.ProtocolMessage.IdTokenHint = store.Find(sessionId)?.Tokens.IdToken;
            store.Remove(sessionId);
        }

        return Task.CompletedTask;
    }
}
