using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Admin.Authentication;

// No hay endpoint de login: toda página exige sesión, y la autorización manda a Keycloak sola.
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/logout", LogoutAsync).RequireAuthorization();
        return app;
    }

    // Un POST con token antiforgery: otra página no puede cerrarte la sesión con un simple link o <img>.
    // La revisión es explícita porque Minimal APIs solo la hace sola cuando el endpoint lee un [FromForm].
    private static async Task<Results<SignOutHttpResult, BadRequest>> LogoutAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return TypedResults.BadRequest();
        }

        // Cierra las dos sesiones: la cookie del panel y la de Keycloak (AdminOidcEvents borra los tokens).
        return TypedResults.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
    }
}
