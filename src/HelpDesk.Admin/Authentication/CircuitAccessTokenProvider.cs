using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Kiota.Abstractions.Authentication;

namespace HelpDesk.Admin.Authentication;

// Kiota le pide el token a esta clase antes de cada llamada a la API.
//
// Es Scoped: en Blazor Server hay un scope por circuito (no por petición), así que AuthenticationStateProvider es el
// del usuario de este circuito. Por eso no se usa un DelegatingHandler de IHttpClientFactory: los handlers viven en
// otro scope y no verían al usuario. Tampoco sirve IHttpContextAccessor: en un circuito no hay un HttpContext válido.
public sealed class CircuitAccessTokenProvider(
    AuthenticationStateProvider authenticationState,
    SessionAccessTokens accessTokens) : IAccessTokenProvider
{
    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public async Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        AuthenticationState state = await authenticationState.GetAuthenticationStateAsync();
        string? sessionId = state.User.FindFirstValue(AdminAuthentication.SessionIdClaim);

        string? accessToken = sessionId is null ? null : await accessTokens.GetAsync(sessionId, cancellationToken);

        return accessToken ?? throw new SessionEndedException();
    }
}
