using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// Asíncrono aunque hoy los datos estén en memoria: la interfaz se diseña para la implementación
// real con EF Core (Fase 3), que sí hará I/O contra la base de datos.
public interface ITicketRepository
{
    Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, CancellationToken cancellationToken);

    Task<Ticket?> FindAsync(TicketId id, CancellationToken cancellationToken);
}
