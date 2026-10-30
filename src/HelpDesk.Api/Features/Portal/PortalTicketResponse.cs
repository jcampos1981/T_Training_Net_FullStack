using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Portal;

// Lo que ve el cliente de su ticket. Un contrato aparte del de soporte (TicketResponse): no expone datos internos
// (a qué agente está asignado, la versión para concurrencia) y puede cambiar sin afectar al panel del equipo.
public sealed record PortalTicketResponse(
    int Id,
    string Code,
    string Title,
    TicketPriority Priority,
    TicketStatus Status,
    DateTimeOffset CreatedAt)
{
    public static PortalTicketResponse From(Ticket ticket) =>
        new(ticket.Id.Value, ticket.Id.ToString(), ticket.Title, ticket.Priority, ticket.Status, ticket.CreatedAt);
}
