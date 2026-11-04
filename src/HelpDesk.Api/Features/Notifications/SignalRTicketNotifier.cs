using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Agents;
using HelpDesk.Api.Features.Sla;
using HelpDesk.Domain;
using Microsoft.AspNetCore.SignalR;

namespace HelpDesk.Api.Features.Notifications;

// IHubContext es la forma de hablarles a los clientes desde fuera del hub. Es singleton, igual que esta clase.
public sealed class SignalRTicketNotifier(
    IHubContext<TicketEventsHub, ITicketEventsClient> hub,
    TimeProvider clock,
    ILogger<SignalRTicketNotifier> logger) : ITicketNotifier
{
    public async Task TicketChangedAsync(Ticket ticket, TicketEventKind kind, ClaimsPrincipal actor, AgentName? previousAgent = null)
    {
        DateTimeOffset now = clock.GetUtcNow();

        try
        {
            await hub.Clients.Groups(TicketAudience.StaffFor(ticket, previousAgent)).TicketChanged(new TicketEvent(
                ticket.Id.Value, ticket.Id.ToString(), ticket.Title, ticket.Status, ticket.AssignedTo?.Value, kind, actor.AuthorName, now));

            // A quién está asignado no es parte del contrato del cliente: un cambio solo de agente no le interesa.
            if (ticket.RequestedBy is { } customer && kind is not TicketEventKind.Reassigned)
            {
                bool bySupport = !string.Equals(actor.Email, customer, StringComparison.OrdinalIgnoreCase);

                await hub.Clients.Group(TicketAudience.Customer(customer)).MyTicketChanged(new PortalTicketEvent(
                    ticket.Id.Value, ticket.Id.ToString(), ticket.Title, ticket.Status, kind, bySupport, now));
            }
        }
        catch (Exception exception)
        {
            // Atrapar todo es a propósito: ver ITicketNotifier. Los clientes se ponen al día al reconectarse.
            logger.LogWarning(exception, "No se pudo avisar el cambio {Kind} del ticket {TicketId}.", kind, ticket.Id);
        }
    }

    public async Task TicketsReassignedAsync(ReassignTicketsResponse reassigned, ClaimsPrincipal actor)
    {
        if (reassigned.ReassignedTickets == 0)
        {
            return;
        }

        try
        {
            string[] audience = [TicketAudience.Supervision, TicketAudience.Agent(reassigned.From), TicketAudience.Agent(reassigned.To)];

            await hub.Clients.Groups(audience).TicketsReassigned(new TicketsReassignedEvent(
                reassigned.From, reassigned.To, reassigned.ReassignedTickets, actor.AuthorName, clock.GetUtcNow()));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "No se pudo avisar la reasignación de {From} a {To}.", reassigned.From, reassigned.To);
        }
    }

    // Supervisión recibe el total, y cada agente solo lo suyo. El cliente no: el SLA es un compromiso interno del equipo.
    public async Task SlaAlertsRaisedAsync(IReadOnlyCollection<RaisedSlaAlert> alerts)
    {
        DateTimeOffset now = clock.GetUtcNow();

        try
        {
            await hub.Clients.Group(TicketAudience.Supervision).SlaAlertsRaised(SlaAlertsEvent.From(alerts, now));

            IEnumerable<IGrouping<string, RaisedSlaAlert>> byAgent = alerts
                .Where(alert => alert.AssignedTo is not null)
                .GroupBy(alert => alert.AssignedTo!, StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, RaisedSlaAlert> agentAlerts in byAgent)
            {
                await hub.Clients.Group(TicketAudience.Agent(agentAlerts.Key)).SlaAlertsRaised(SlaAlertsEvent.From([.. agentAlerts], now));
            }
        }
        catch (Exception exception)
        {
            // Las alertas ya están guardadas: siguen disponibles en GET /api/sla/alerts.
            logger.LogWarning(exception, "No se pudieron avisar {Count} alertas de SLA.", alerts.Count);
        }
    }
}
