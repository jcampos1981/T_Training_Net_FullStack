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
    TimeProvider clock,
    ILogger<BffCookieEvents> logger) : CookieAuthenticationEvents
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

        CancellationToken aborted = context.HttpContext.RequestAborted;
        RefreshedTokens? tokens;
        try
        {
            tokens = await refresher.RefreshAsync(refreshToken, aborted);
        }
        catch (Exception exception) when (IsKeycloakOutage(exception, aborted))
        {
            // Que Keycloak no responda no es lo mismo que rechace la sesión: cerrarla sacaría a todos los usuarios por
            // una caída de segundos. Se sigue con el token actual, y la próxima petición vuelve a intentar renovarlo.
            logger.LogWarning(exception, "Keycloak no responde; la sesión sigue y se reintenta en la próxima petición.");
            return;
        }

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

    // Sin red o 5xx (HttpRequestException), discovery document no disponible (InvalidOperationException) o tiempo de
    // espera agotado. Si canceló el navegador al cerrar la pestaña, no es una caída.
    private static bool IsKeycloakOutage(Exception exception, CancellationToken aborted) =>
        exception is HttpRequestException or InvalidOperationException
        || (exception is OperationCanceledException && !aborted.IsCancellationRequested);
}
