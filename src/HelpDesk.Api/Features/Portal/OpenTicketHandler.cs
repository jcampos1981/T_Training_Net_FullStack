using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Portal;

// Caso de uso "un cliente abre un ticket": el ticket y su descripción (primer comentario) se guardan juntos.
public sealed class OpenTicketHandler(HelpDeskDbContext db, ITicketRepository tickets, TimeProvider clock)
{
    public async Task<Ticket> HandleAsync(
        OpenTicketRequest request,
        string customerEmail,
        string author,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();

        var ticket = new Ticket
        {
            // El Id se reserva antes (SEQUENCE): así el comentario puede apuntar al ticket antes de guardar nada.
            Id = await tickets.NextIdAsync(cancellationToken),
            Title = request.Title,
            Priority = request.Priority,
            CreatedAt = now,
            RequestedBy = customerEmail
        };

        TicketComment description = ticket.CreateComment(author, request.Description, now);

        // Un solo SaveChanges = una sola transacción: se guardan los dos o ninguno. No hace falta abrir una a
        // mano (como en la reasignación masiva), porque aquí no hay dos sentencias separadas que coordinar.
        db.Tickets.Add(ticket);
        db.Comments.Add(description);
        await db.SaveChangesAsync(cancellationToken);

        return ticket;
    }
}
