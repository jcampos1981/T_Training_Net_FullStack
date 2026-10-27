using HelpDesk.Api.Features.Accounts.Tokens;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Data;

// El DbContext es la "sesión" con la base de datos: sabe qué tablas hay y lleva el registro
// de los cambios pendientes hasta que se llama a SaveChangesAsync (patrón Unit of Work).
// IdentityDbContext agrega las tablas de ASP.NET Core Identity (AspNetUsers, AspNetRoles...): usuarios,
// roles y claims viven en la misma base que los tickets.
public sealed class HelpDeskDbContext(DbContextOptions<HelpDeskDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public const string TicketIdSequence = "TicketIds";

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketComment> Comments => Set<TicketComment>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    // Convenciones: reglas que aplican a TODAS las propiedades de un tipo, en cualquier entidad.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<TicketId>().HaveConversion<TicketIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Primero Identity: configura sus tablas, llaves e índices. Sin esta línea, sus entidades quedan sin llave.
        base.OnModelCreating(modelBuilder);

        // Los Ids de ticket salen de una SEQUENCE: se reservan ANTES de insertar, porque
        // TicketId no admite valores vacíos (un IDENTITY solo da el valor después del INSERT).
        modelBuilder.HasSequence<int>(TicketIdSequence);

        // Busca todas las clases IEntityTypeConfiguration<T> de este proyecto (una por tabla).
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HelpDeskDbContext).Assembly);
    }
}
