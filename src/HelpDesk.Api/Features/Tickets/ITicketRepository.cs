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

    // original: el ticket tal como se leyó · changed: con los cambios · expectedVersion: la versión que vio el cliente.
    // Devuelve el ticket guardado, con su nueva Version. Si otro guardó antes, lanza DbUpdateConcurrencyException.
    Task<Ticket> UpdateAsync(Ticket original, Ticket changed, ulong expectedVersion, CancellationToken cancellationToken);

    // Cambio masivo: un solo UPDATE en la base, sin cargar los tickets. Devuelve cuántos cambiaron.
    Task<int> ReassignOpenTicketsAsync(string fromAgent, string toAgent, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(TicketId id, CancellationToken cancellationToken);
}
