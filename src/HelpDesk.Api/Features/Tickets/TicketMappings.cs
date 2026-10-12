using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// Mapeo manual: explícito, rápido y sin depender de AutoMapper (que pasó a licencia comercial).
public static class TicketMappings
{
    extension(Ticket ticket)
    {
        public TicketResponse ToResponse() => new(
            ticket.Id.Value,
            ticket.Id.ToString(),
            ticket.Title,
            ticket.Priority,
            ticket.Status,
            ticket.CreatedAt,
            ticket.AssignedTo);
    }
}
