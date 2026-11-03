namespace HelpDesk.Admin.Realtime;

// Los avisos que manda la API. El hub no aparece en el OpenAPI, así que Kiota no los genera y se mantienen a mano: si la
// API cambia el contrato, el compilador no se entera. Solo las pruebas de punta a punta lo detectan.
public enum TicketEventKind
{
    Created,
    Updated,
    Reassigned,
    Commented,
    Deleted
}

public sealed record TicketEvent(
    int TicketId,
    string Code,
    string Title,
    string? AssignedTo,
    TicketEventKind Kind,
    string Actor,
    DateTimeOffset OccurredAt);

public sealed record TicketsReassignedEvent(
    string From,
    string To,
    int Tickets,
    string Actor,
    DateTimeOffset OccurredAt);

// Los totales de quien lo recibe. Ticket solo viene si la alerta es una sola.
public sealed record SlaAlertsEvent(int AtRisk, int Breached, SlaAlertedTicket? Ticket, DateTimeOffset OccurredAt);

public sealed record SlaAlertedTicket(int Id, string Code, string Title);
