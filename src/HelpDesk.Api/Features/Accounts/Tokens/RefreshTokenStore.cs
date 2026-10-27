using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using HelpDesk.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Features.Accounts.Tokens;

// Refresh tokens con rotación: cada uno sirve UNA vez y se cambia por otro de la misma familia.
// Si llega uno que ya se usó, alguien tiene una copia (robo, o un cliente con un bug). No se sabe cuál de los dos
// es el legítimo, así que se revoca la familia entera y ambos vuelven a iniciar sesión (OAuth 2.0 Security BCP).
public sealed partial class RefreshTokenStore(
    HelpDeskDbContext db,
    IOptions<JwtOptions> options,
    TimeProvider clock,
    ILogger<RefreshTokenStore> logger)
{
    // Emite uno nuevo. Devuelve el valor para el cliente: es lo único que no queda guardado.
    public async Task<string> CreateAsync(IdentityUser user, Guid familyId, CancellationToken cancellationToken)
    {
        // 32 bytes de un generador criptográfico: imposible de adivinar, a diferencia de un Guid o de Random.
        string value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset now = clock.GetUtcNow();

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(value),
            FamilyId = familyId,
            SecurityStamp = user.SecurityStamp ?? string.Empty,
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.RefreshTokenDays)
        });
        await db.SaveChangesAsync(cancellationToken);

        return value;
    }

    // Canjea un refresh token: lo marca como usado y devuelve sus datos, o null si no sirve.
    public async Task<RefreshToken?> RedeemAsync(string value, CancellationToken cancellationToken)
    {
        byte[] hash = Hash(value);
        DateTimeOffset now = clock.GetUtcNow();

        RefreshToken? stored = await db.RefreshTokens.AsNoTracking()
            .FirstOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);

        if (stored is null || stored.RevokedAt is not null || stored.ExpiresAt <= now)
        {
            return null;
        }

        // Canje atómico (el patrón de la Fase 4): de dos peticiones con el mismo token, solo una consigue que el
        // UPDATE encuentre "UsedAt IS NULL". Leer y después escribir dejaría pasar a las dos.
        int redeemed = await db.RefreshTokens
            .Where(token => token.Id == stored.Id && token.UsedAt == null && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.UsedAt, now), cancellationToken);

        if (redeemed == 0)
        {
            await RevokeFamilyAsync(stored.FamilyId, cancellationToken);
            LogReuseDetected(logger, stored.UserId, stored.FamilyId);
            return null;
        }

        return stored;
    }

    // Cerrar sesión: se revoca la familia completa del token que manda el cliente.
    public async Task RevokeAsync(string value, CancellationToken cancellationToken)
    {
        byte[] hash = Hash(value);
        Guid? familyId = await db.RefreshTokens
            .Where(token => token.TokenHash == hash)
            .Select(token => (Guid?)token.FamilyId)
            .FirstOrDefaultAsync(cancellationToken);

        if (familyId is { } family)
        {
            await RevokeFamilyAsync(family, cancellationToken);
        }
    }

    public Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return db.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh token reutilizado: usuario {UserId}, familia {FamilyId} revocada completa.")]
    private static partial void LogReuseDetected(ILogger logger, string userId, Guid familyId);
}
