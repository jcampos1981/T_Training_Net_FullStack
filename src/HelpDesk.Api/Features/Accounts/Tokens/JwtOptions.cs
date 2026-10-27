using System.ComponentModel.DataAnnotations;
using Microsoft.IdentityModel.Tokens;

namespace HelpDesk.Api.Features.Accounts.Tokens;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    // Quién emite el token (iss) y para quién es (aud): la API rechaza tokens de otro emisor o para otra audiencia.
    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    // Clave para firmar con HS256 (HMAC-SHA256), en Base64. Va en User Secrets en desarrollo y en un almacén de
    // secretos en producción, nunca en appsettings. Con HS256 la MISMA clave firma y valida: quien pueda validar
    // también puede emitir. Si otros servicios tienen que validar, conviene RS256/ES256 (clave pública): Bloque 4.
    [Required]
    public string SigningKey { get; set; } = string.Empty;

    // Corto a propósito: un JWT no se puede revocar, solo vence.
    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 14;

    public SymmetricSecurityKey CreateSigningKey() => new(Convert.FromBase64String(SigningKey));

    // HS256 exige al menos 256 bits (32 bytes) de clave; con menos, la librería se niega a firmar.
    public bool HasValidSigningKey()
    {
        Span<byte> buffer = stackalloc byte[SigningKey.Length];
        return Convert.TryFromBase64String(SigningKey, buffer, out int bytes) && bytes >= 32;
    }
}
