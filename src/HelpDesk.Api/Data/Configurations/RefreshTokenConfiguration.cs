using HelpDesk.Api.Features.Accounts.Tokens;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HelpDesk.Api.Data.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(token => token.Id);

        // binary(32): el SHA-256 en bytes ocupa la mitad que en texto hexadecimal. Cada refresh busca por aquí.
        builder.Property(token => token.TokenHash).HasMaxLength(32).IsFixedLength();
        builder.HasIndex(token => token.TokenHash).IsUnique();

        // Para revocar una familia completa cuando se detecta un token reutilizado.
        builder.HasIndex(token => token.FamilyId);

        builder.Property(token => token.SecurityStamp).HasMaxLength(100);

        // Borrar un usuario borra sus refresh tokens.
        builder.HasOne<IdentityUser>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
