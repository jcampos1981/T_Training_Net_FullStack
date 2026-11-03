namespace HelpDesk.Api.Features.Notifications;

// La reasignación masiva: un solo aviso, aunque mueva miles de tickets. Uno por ticket sería una avalancha.
public sealed record TicketsReassignedEvent(
    string From,
    string To,
    int Tickets,
    string Actor,
    DateTimeOffset OccurredAt);
