using HelpDesk.Api.Features.Sla;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HelpDesk.Api.Data.Configurations;

public sealed class SlaAlertConfiguration : IEntityTypeConfiguration<SlaAlert>
{
    public void Configure(EntityTypeBuilder<SlaAlert> builder)
    {
        builder.ToTable("SlaAlerts");
        builder.HasKey(alert => alert.Id);
        builder.Property(alert => alert.Id).UseIdentityColumn();

        builder.Property(alert => alert.Level).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(alert => alert.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // Una alerta por ticket y nivel, garantizado por la base aunque corran varias instancias de la API. En la tabla
        // lleva además IGNORE_DUP_KEY (SlaAlertScanner lo necesita), que EF no sabe expresar: lo crea la migración con
        // SQL a mano. Si otra migración toca este índice, hay que volver a incluir esa opción.
        builder.HasIndex(alert => new { alert.TicketId, alert.Level }).IsUnique();
    }
}
