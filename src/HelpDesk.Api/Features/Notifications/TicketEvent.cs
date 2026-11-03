using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Notifications;

// El aviso que recibe el equipo. Es una señal, no los datos: dice qué ticket cambió, y la pantalla vuelve a pedirlo por
// la API, donde se aplican los permisos de siempre. Lo que trae es solo lo necesario para el mensaje del aviso.
public sealed record TicketEvent(
    int TicketId,
    string Code,
    string Title,
    TicketStatus Status,
    string? AssignedTo,
    TicketEventKind Kind,
    string Actor,
    DateTimeOffset OccurredAt);
