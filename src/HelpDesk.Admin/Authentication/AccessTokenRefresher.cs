using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace HelpDesk.Admin.Authentication;

// Cambia el refresh token por tokens nuevos en el token endpoint de Keycloak (grant_type=refresh_token).
public sealed class AccessTokenRefresher(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TimeProvider clock,
    ILogger<AccessTokenRefresher> logger)
{
    // null si Keycloak se niega: la sesión terminó allá (logout en otro sitio, sesión vencida, usuario bloqueado).
    // Si Keycloak está caído (5xx o sin red) lanza una excepción: la sesión sigue viva y se puede reintentar.
    public async Task<SessionTokens?> RefreshAsync(SessionTokens current, CancellationToken cancellationToken)
    {
        // Las mismas opciones (y el mismo HttpClient) que usa el handler de OpenID Connect para el login.
        OpenIdConnectOptions oidc = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        OpenIdConnectConfiguration configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken);

        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["client_id"] = oidc.ClientId!,
            ["client_secret"] = oidc.ClientSecret!
        });

        using HttpResponseMessage response = await oidc.Backchannel.PostAsync(configuration.TokenEndpoint, request, cancellationToken);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            logger.LogWarning("Keycloak no renovó el access token ({Status}); la sesión terminó.", (int)response.StatusCode);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var tokens = new OpenIdConnectMessage(await response.Content.ReadAsStringAsync(cancellationToken));
        DateTimeOffset expiresAt = ExpirationOf(tokens, clock);

        logger.LogInformation("Access token renovado; vence a las {ExpiresAt:HH:mm:ss} UTC.", expiresAt);

        // Keycloak puede devolver un refresh token nuevo; si no, se sigue usando el anterior.
        return new SessionTokens(
            tokens.AccessToken,
            tokens.RefreshToken ?? current.RefreshToken,
            tokens.IdToken ?? current.IdToken,
            expiresAt);
    }

    // Sin expires_in se toma como vencido: la próxima llamada vuelve a renovar.
    public static DateTimeOffset ExpirationOf(OpenIdConnectMessage tokens, TimeProvider clock)
    {
        int lifetimeSeconds = int.TryParse(tokens.ExpiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) ? seconds : 0;
        return clock.GetUtcNow().AddSeconds(lifetimeSeconds);
    }
}
