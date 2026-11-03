using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Agents;
using HelpDesk.Domain;
using Microsoft.AspNetCore.SignalR;

namespace HelpDesk.Api.Features.Notifications;

// IHubContext es la forma de hablarles a los clientes desde fuera del hub. Es singleton, igual que esta clase.
public sealed class SignalRTicketNotifier(
    IHubContext<TicketEventsHub, ITicketEventsClient> hub,
    TimeProvider clock,
    ILogger<SignalRTicketNotifier> logger) : ITicketNotifier
{
    public async Task TicketChangedAsync(Ticket ticket, TicketEventKind kind, ClaimsPrincipal actor, string? previousAgent = null)
    {
        DateTimeOffset now = clock.GetUtcNow();

        try
        {
            await hub.Clients.Groups(TicketAudience.StaffFor(ticket, previousAgent)).TicketChanged(new TicketEvent(
                ticket.Id.Value, ticket.Id.ToString(), ticket.Title, ticket.Status, ticket.AssignedTo, kind, actor.AuthorName, now));

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
}
