using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// Filtros opcionales del query string: /api/tickets?status=Open&priority=High&assignedTo=ana&requestedBy=...
// Solo datos: el repositorio los traduce a WHERE de SQL (un método C# aquí no se podría traducir).
public sealed record TicketFilter(TicketStatus? Status, TicketPriority? Priority, string? AssignedTo, string? RequestedBy = null);
