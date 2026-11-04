using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HelpDesk.Api.Outbox;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Type).HasMaxLength(100);
        builder.Property(message => message.TraceParent).HasMaxLength(100);

        // Solo las pendientes, que son pocas. Publicar pregunta por ellas cada pocos segundos, y la tabla puede tener millones de ya publicadas.
        builder.HasIndex(message => message.Id)
            .HasDatabaseName("IX_OutboxMessages_Pending")
            .HasFilter("[PublishedAt] IS NULL");

        builder.HasIndex(message => message.PublishedAt)
            .HasDatabaseName("IX_OutboxMessages_Published")
            .HasFilter("[PublishedAt] IS NOT NULL");
    }
}
