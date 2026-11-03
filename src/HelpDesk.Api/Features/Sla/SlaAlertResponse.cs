using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Sla;

public sealed record SlaAlertResponse(
    int Id,
    int TicketId,
    string Code,
    string Title,
    TicketPriority Priority,
    TicketStatus Status,
    string? AssignedTo,
    SlaState Level,
    DateTimeOffset Deadline,
    DateTimeOffset RaisedAt);
