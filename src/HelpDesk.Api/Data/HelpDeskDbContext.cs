using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Data;

// El DbContext es la "sesión" con la base de datos: sabe qué tablas hay y lleva el registro
// de los cambios pendientes hasta que se llama a SaveChangesAsync (patrón Unit of Work).
public sealed class HelpDeskDbContext(DbContextOptions<HelpDeskDbContext> options) : DbContext(options)
{
    public const string TicketIdSequence = "TicketIds";

    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Los Ids de ticket salen de una SEQUENCE: se reservan ANTES de insertar, porque
        // TicketId no admite valores vacíos (un IDENTITY solo da el valor después del INSERT).
        modelBuilder.HasSequence<int>(TicketIdSequence);

        // Busca todas las clases IEntityTypeConfiguration<T> de este proyecto (una por tabla).
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HelpDeskDbContext).Assembly);
    }
}
