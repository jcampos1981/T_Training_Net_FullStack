using System.Globalization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace HelpDesk.Bff.Authentication;

public sealed record RefreshedTokens(string AccessToken, string RefreshToken, string? IdToken, DateTimeOffset ExpiresAt);

// Cambia el refresh token por tokens nuevos en el token endpoint de Keycloak (grant_type=refresh_token).
// Lo hace el servidor, con el secreto del client: el navegador nunca ve ni el refresh token ni el secreto.
public sealed class AccessTokenRefresher(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TimeProvider clock,
    ILogger<AccessTokenRefresher> logger)
{
    // null si Keycloak se niega: la sesión terminó allá (logout en otra pestaña, sesión vencida, usuario bloqueado).
    public async Task<RefreshedTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        // Las mismas opciones (y el mismo HttpClient) que usa el handler de OpenID Connect para el login.
        OpenIdConnectOptions oidc = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        OpenIdConnectConfiguration configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken);

        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = oidc.ClientId!,
            ["client_secret"] = oidc.ClientSecret!
        });

        using HttpResponseMessage response = await oidc.Backchannel.PostAsync(configuration.TokenEndpoint, request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Keycloak no renovó el access token ({Status}); se cierra la sesión del BFF.", (int)response.StatusCode);
            return null;
        }

        // OpenIdConnectMessage entiende la respuesta JSON del token endpoint (access_token, expires_in...).
        // Sin expires_in se toma como vencido: la próxima petición vuelve a renovar.
        var tokens = new OpenIdConnectMessage(body);
        int lifetimeSeconds = int.TryParse(tokens.ExpiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) ? seconds : 0;
        DateTimeOffset expiresAt = clock.GetUtcNow().AddSeconds(lifetimeSeconds);

        logger.LogInformation("Access token renovado; vence a las {ExpiresAt:HH:mm:ss} UTC.", expiresAt);

        // Keycloak puede devolver un refresh token nuevo; si no, se sigue usando el anterior.
        return new RefreshedTokens(tokens.AccessToken, tokens.RefreshToken ?? refreshToken, tokens.IdToken, expiresAt);
    }
}
