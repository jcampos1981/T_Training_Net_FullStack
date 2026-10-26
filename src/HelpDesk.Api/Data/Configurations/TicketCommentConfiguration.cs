using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HelpDesk.Api.Data.Configurations;

public sealed class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> builder)
    {
        builder.ToTable("TicketComments");
        builder.HasKey(comment => comment.Id);

        // A diferencia de Tickets, aquí el Id lo genera SQL Server (IDENTITY) al insertar.
        builder.Property(comment => comment.Id).UseIdentityColumn();

        builder.Property(comment => comment.Author).HasMaxLength(50).IsRequired();
        builder.Property(comment => comment.Body).HasMaxLength(2000).IsRequired();

        // Relación 1-N sin propiedades de navegación: solo la llave foránea.
        // Cascade: al borrar un ticket, la base borra sus comentarios.
        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(comment => comment.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // Las consultas siempre piden "los comentarios de un ticket, del más reciente al más viejo".
        builder.HasIndex(comment => new { comment.TicketId, comment.CreatedAt });
    }
}
