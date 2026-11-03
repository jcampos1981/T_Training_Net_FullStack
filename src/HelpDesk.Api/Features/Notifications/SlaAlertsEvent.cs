using HelpDesk.Api.Features.Sla;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Notifications;

// Un solo aviso por vuelta del monitor, con los totales de quien lo recibe: el primer arranque crea decenas de miles de
// alertas, y un aviso por cada una sería una avalancha. Ticket solo viene si la alerta es una sola.
public sealed record SlaAlertsEvent(int AtRisk, int Breached, SlaAlertedTicket? Ticket, DateTimeOffset OccurredAt)
{
    public static SlaAlertsEvent From(IReadOnlyCollection<RaisedSlaAlert> alerts, DateTimeOffset now)
    {
        RaisedSlaAlert? only = alerts.Count == 1 ? alerts.First() : null;

        return new SlaAlertsEvent(
            alerts.Count(alert => alert.Level is SlaState.AtRisk),
            alerts.Count(alert => alert.Level is SlaState.Breached),
            only is null ? null : new SlaAlertedTicket(only.TicketId, new TicketId(only.TicketId).ToString(), only.Title),
            now);
    }
}

public sealed record SlaAlertedTicket(int Id, string Code, string Title);
