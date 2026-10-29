using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Bff.Endpoints;

public sealed record BffUser(string Name, string? Email, string LogoutUrl);

// Lo que el frontend necesita del BFF: entrar, saber quién es y salir. Los tokens no salen nunca de aquí.
public static class BffEndpoints
{
    // "sid": el id de la sesión de Keycloak, viene en el ID token.
    private const string SessionIdClaim = "sid";

    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder bff = app.MapGroup("/bff");

        // Es una navegación del navegador (un link), no un fetch: termina en la página de login de Keycloak.
        bff.MapGet("/login", Login);
        bff.MapGet("/user", GetUser).RequireAuthorization();
        bff.MapGet("/logout", Logout).RequireAuthorization();

        return app;
    }

    private static ChallengeHttpResult Login(string? returnUrl) =>
        TypedResults.Challenge(
            new AuthenticationProperties { RedirectUri = LocalOrRoot(returnUrl) },
            [OpenIdConnectDefaults.AuthenticationScheme]);

    // El frontend llama a esto al cargar: 200 con el usuario, o 401 si no hay sesión (y muestra "Iniciar sesión").
    // Los roles y el agente los da GET /api/me: la API es la fuente de verdad de lo que el usuario puede hacer.
    private static Ok<BffUser> GetUser(ClaimsPrincipal user) =>
        TypedResults.Ok(new BffUser(
            user.Identity?.Name ?? string.Empty,
            user.FindFirstValue("email"),
            $"/bff/logout?sid={Uri.EscapeDataString(user.FindFirstValue(SessionIdClaim) ?? string.Empty)}"));

    // Cierra las dos sesiones: la cookie del BFF y la de Keycloak (redirige a su end_session_endpoint y vuelve).
    // El "sid" en la URL evita el logout por CSRF: otra página no conoce el sid de tu sesión, así que no puede
    // cerrarla con un simple <img src="https://localhost:7200/bff/logout">.
    private static Results<SignOutHttpResult, ProblemHttpResult> Logout(string? sid, ClaimsPrincipal user)
    {
        string? sessionId = user.FindFirstValue(SessionIdClaim);
        if (sessionId is null || !string.Equals(sid, sessionId, StringComparison.Ordinal))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Logout inválido",
                detail: "Usa la logoutUrl que devuelve GET /bff/user.");
        }

        return TypedResults.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
    }

    // Evita el "open redirect": /bff/login?returnUrl=https://sitio-falso.com dejaría al usuario, recién
    // autenticado, en otro sitio. Solo se aceptan rutas locales ("/tickets"), no "//host" ni "/\host".
    private static string LocalOrRoot(string? url) =>
        url is not null && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
