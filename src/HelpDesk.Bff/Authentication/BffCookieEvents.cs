using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace HelpDesk.Bff.Authentication;

// Qué hace la cookie del BFF en cada petición:
// - si el access token guardado está por vencer, lo renueva antes de reenviar la llamada a la API;
// - sin sesión responde 401 (y sin permiso, 403) en vez de redirigir a una página de login: quien pregunta es el
//   JavaScript del frontend, que decide si manda al usuario a /bff/login.
public sealed class BffCookieEvents(
    AccessTokenRefresher refresher,
    IOptions<KeycloakClientOptions> options,
    TimeProvider clock) : CookieAuthenticationEvents
{
    private const string AccessToken = "access_token";
    private const string RefreshToken = "refresh_token";
    private const string IdToken = "id_token";
    private const string ExpiresAt = "expires_at";

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        // SaveTokens = true dejó los tokens dentro de la cookie, cifrados con Data Protection: JS no puede leerlos.
        string? expiresAt = context.Properties.GetTokenValue(ExpiresAt);
        string? refreshToken = context.Properties.GetTokenValue(RefreshToken);

        if (expiresAt is null || refreshToken is null || !IsAboutToExpire(expiresAt))
        {
            return;
        }

        RefreshedTokens? tokens = await refresher.RefreshAsync(refreshToken, context.HttpContext.RequestAborted);

        if (tokens is null)
        {
            // Keycloak ya no reconoce la sesión: se borra la cookie y esta petición sigue como anónima (→ 401).
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        context.Properties.UpdateTokenValue(AccessToken, tokens.AccessToken);
        context.Properties.UpdateTokenValue(RefreshToken, tokens.RefreshToken);
        context.Properties.UpdateTokenValue(ExpiresAt, tokens.ExpiresAt.ToString("o", CultureInfo.InvariantCulture));
        if (tokens.IdToken is not null)
        {
            context.Properties.UpdateTokenValue(IdToken, tokens.IdToken);
        }

        context.ShouldRenew = true; // vuelve a emitir la cookie, ahora con los tokens nuevos
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private bool IsAboutToExpire(string expiresAt) =>
        DateTimeOffset.Parse(expiresAt, CultureInfo.InvariantCulture) - clock.GetUtcNow() <= options.Value.RefreshBeforeExpiry;
}
