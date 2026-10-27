using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HelpDesk.Api.Features.Accounts.Tokens;

public sealed record AccessToken(string Value, int ExpiresInSeconds);

// Arma y firma el JWT de acceso: encabezado.contenido.firma, cada parte en Base64Url. El contenido se puede
// leer (no está cifrado, solo firmado): nunca se ponen datos secretos en un JWT.
public sealed class AccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider clock)
{
    // Nombre corto, como lo usan los JWT. Al validar, JwtBearer lo traduce a ClaimTypes.Role.
    private const string RoleClaimType = "role";

    private readonly JsonWebTokenHandler handler = new();

    public AccessToken Create(IdentityUser user, IEnumerable<string> roles, IEnumerable<Claim> userClaims)
    {
        JwtOptions jwt = options.Value;
        DateTime now = clock.GetUtcNow().UtcDateTime;

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id),                     // a quién pertenece
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),   // id único de ESTE token
            .. roles.Select(role => new Claim(RoleClaimType, role)),
            .. userClaims                                                  // por ejemplo, helpdesk:agent
        ];

        string token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(jwt.AccessTokenMinutes),
            SigningCredentials = new SigningCredentials(jwt.CreateSigningKey(), SecurityAlgorithms.HmacSha256)
        });

        return new AccessToken(token, jwt.AccessTokenMinutes * 60);
    }
}
