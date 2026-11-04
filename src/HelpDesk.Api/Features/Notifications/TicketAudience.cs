using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Notifications;

// Quién debe enterarse de qué, expresado como grupos de SignalR. Son las mismas reglas de lectura de la API: un agente
// ve lo suyo, supervisión ve todo y un cliente ve sus tickets.
public static class TicketAudience
{
    public const string Supervision = "supervision";

    // Los nombres de grupo distinguen mayúsculas, y SQL Server no: "Pedro" y "pedro" son el mismo agente.
    public static string Agent(string name) => $"agent:{name.ToLowerInvariant()}";

    public static string Customer(string email) => $"customer:{email.ToLowerInvariant()}";

    // A qué grupos pertenece un usuario. Salen de su token: el cliente no puede pedir entrar a otro.
    public static IEnumerable<string> GroupsOf(ClaimsPrincipal user)
    {
        if (user.IsSupervisor)
        {
            yield return Supervision;
        }

        if (user.Agent is { } agent)
        {
            yield return Agent(agent.Value);
        }

        if (user.IsInRole(HelpDeskRoles.Customer) && user.Email is { } email)
        {
            yield return Customer(email);
        }
    }

    // previousAgent: si el ticket cambió de manos, quien lo tenía también debe enterarse.
    public static IReadOnlyList<string> StaffFor(Ticket ticket, AgentName? previousAgent)
    {
        IEnumerable<string> agents = new[] { ticket.AssignedTo, previousAgent }
            .OfType<AgentName>()
            .Distinct()
            .Select(agent => Agent(agent.Value));

        return [Supervision, .. agents];
    }
}
