using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Notifications;

// El aviso que recibe el cliente. Otro contrato que el del equipo, por la misma razón que PortalTicketResponse: no dice
// a qué agente está asignado ni quién hizo el cambio. Solo si vino de soporte o de él mismo (en otra pestaña).
public sealed record PortalTicketEvent(
    int TicketId,
    string Code,
    string Title,
    TicketStatus Status,
    TicketEventKind Kind,
    bool BySupport,
    DateTimeOffset OccurredAt);
