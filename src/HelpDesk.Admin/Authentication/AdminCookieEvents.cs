using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace HelpDesk.Admin.Authentication;

// Una cookie válida no alcanza: también tienen que existir sus tokens en el servidor. Si no están (el panel se
// reinició, se cerró la sesión o Keycloak la rechazó), se borra la cookie. La petición sigue como anónima, y la
// autorización manda al usuario a Keycloak: si su sesión allá sigue viva vuelve con tokens nuevos sin notarlo, y si
// no, ve la pantalla de login.
public sealed class AdminCookieEvents(SessionTokenStore store) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        string? sessionId = context.Principal?.FindFirstValue(AdminAuthentication.SessionIdClaim);

        if (sessionId is null || store.Find(sessionId) is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
