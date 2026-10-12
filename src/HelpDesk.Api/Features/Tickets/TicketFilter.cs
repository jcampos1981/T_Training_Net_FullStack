using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// Filtros opcionales del query string: /api/tickets?status=Open&priority=High&assignedTo=ana
public sealed record TicketFilter(TicketStatus? Status, TicketPriority? Priority, string? AssignedTo)
{
    public bool Matches(Ticket ticket) =>
        (Status is null || ticket.Status == Status) &&
        (Priority is null || ticket.Priority == Priority) &&
        (AssignedTo is null || string.Equals(ticket.AssignedTo, AssignedTo, StringComparison.OrdinalIgnoreCase));
}
