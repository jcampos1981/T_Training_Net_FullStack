using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace HelpDesk.Api.Features.Accounts.Tokens;

// Entrega el par de tokens: el JWT de acceso (corto) y el refresh token (largo, de una familia).
// Lo usan el login y el refresh, para que los dos armen la respuesta de la misma forma.
public sealed class TokenService(
    UserManager<IdentityUser> users,
    AccessTokenIssuer accessTokens,
    RefreshTokenStore refreshTokens)
{
    public async Task<TokenResponse> IssueAsync(IdentityUser user, Guid familyId, CancellationToken cancellationToken)
    {
        // Roles y claims se leen de la base en cada emisión: un cambio de rol se nota en el siguiente refresh.
        IList<string> roles = await users.GetRolesAsync(user);
        IList<Claim> claims = await users.GetClaimsAsync(user);

        AccessToken access = accessTokens.Create(user, roles, claims);
        string refresh = await refreshTokens.CreateAsync(user, familyId, cancellationToken);

        return new TokenResponse("Bearer", access.Value, access.ExpiresInSeconds, refresh);
    }
}
