using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HelpDesk.Api.Data.Configurations;

// Fluent API: el mapeo vive aquí y no como atributos en Ticket, así el dominio no sabe que existe una base de datos.
public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets");
        builder.HasKey(ticket => ticket.Id);

        // En C# es un TicketId y en la tabla un int (TicketIdConverter, registrado como convención).
        // ValueGeneratedNever: el Id llega ya asignado desde la SEQUENCE.
        builder.Property(ticket => ticket.Id).ValueGeneratedNever();

        builder.Property(ticket => ticket.Title).HasMaxLength(120).IsRequired();

        // Enums como texto ("High") en vez de número (2): la tabla se entiende sin el código.
        builder.Property(ticket => ticket.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(ticket => ticket.Status).HasConversion<string>().HasMaxLength(20);

        builder.Property(ticket => ticket.AssignedTo).HasMaxLength(50);

        // Índices para los filtros de GET /api/tickets (en la Fase 4 veremos si de verdad se usan).
        builder.HasIndex(ticket => new { ticket.Status, ticket.Priority });
        builder.HasIndex(ticket => ticket.AssignedTo);
    }
}
