using System.Security.Claims;
using HelpDesk.Api.Features.Agents;
using HelpDesk.Api.Features.Sla;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Notifications;

// Los casos de uso avisan que algo cambió sin saber cómo se entrega. Se llama DESPUÉS de guardar, y nunca lanza
// excepciones: el cambio ya está hecho y un aviso perdido no debe convertirlo en un error para quien lo hizo.
public interface ITicketNotifier
{
    Task TicketChangedAsync(Ticket ticket, TicketEventKind kind, ClaimsPrincipal actor, AgentName? previousAgent = null);

    Task TicketsReassignedAsync(ReassignTicketsResponse reassigned, ClaimsPrincipal actor);

    Task SlaAlertsRaisedAsync(IReadOnlyCollection<RaisedSlaAlert> alerts);
}
