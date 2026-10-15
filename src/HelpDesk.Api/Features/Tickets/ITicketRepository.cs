using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// Asíncrono aunque hoy los datos estén en memoria: la interfaz se diseña para la implementación
// real con EF Core (Fase 3), que sí hará I/O contra la base de datos.
public interface ITicketRepository
{
    Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, CancellationToken cancellationToken);

    Task<Ticket?> FindAsync(TicketId id, CancellationToken cancellationToken);

    // El Id se reserva antes de crear el ticket porque TicketId no admite valores vacíos.
    // Con SQL Server se resolverá con una SEQUENCE.
    Task<TicketId> NextIdAsync(CancellationToken cancellationToken);

    Task AddAsync(Ticket ticket, CancellationToken cancellationToken);

    Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(TicketId id, CancellationToken cancellationToken);
}
