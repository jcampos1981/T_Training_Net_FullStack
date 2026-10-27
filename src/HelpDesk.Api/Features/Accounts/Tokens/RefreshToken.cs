namespace HelpDesk.Api.Features.Accounts.Tokens;

// Un refresh token emitido. Del valor solo se guarda el hash: el valor lo tiene únicamente el cliente, así que
// alguien que lea esta tabla no puede usar lo que encuentra (igual que con las contraseñas).
// Los cambios de estado (canjeado, revocado) se hacen con ExecuteUpdate: por eso todo es init.
public sealed class RefreshToken
{
    public Guid Id { get; init; }

    public required string UserId { get; init; }

    // SHA-256 del valor que recibió el cliente.
    public required byte[] TokenHash { get; init; }

    // La cadena de rotaciones que empezó en un login. Si se detecta un token reutilizado, se revoca la familia entera.
    public required Guid FamilyId { get; init; }

    // El security stamp del usuario al emitirlo. Identity lo cambia al cambiar la contraseña: entonces deja de servir.
    public required string SecurityStamp { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    // Cuándo se canjeó por uno nuevo. Cada refresh token sirve UNA sola vez.
    public DateTimeOffset? UsedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }
}
