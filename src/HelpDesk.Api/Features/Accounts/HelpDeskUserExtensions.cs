using System.Security.Claims;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Accounts;

// Lo que la API necesita saber del usuario, leído de sus claims (vengan de un JWT o de Identity).
public static class HelpDeskUserExtensions
{
    extension(ClaimsPrincipal user)
    {
        // El nombre con el que aparece en Ticket.AssignedTo ("pedro"), o null si no es agente.
        public AgentName? Agent => AgentName.From(user.FindFirstValue(HelpDeskClaimTypes.Agent));

        // El email, igual venga de la cookie de Identity, de nuestro JWT o de Keycloak. Identifica al cliente en
        // Ticket.RequestedBy. (Más robusto sería el "sub", que no cambia si el usuario cambia su email; el email
        // se eligió porque en la tabla se lee solo.)
        public string? Email => user.FindFirstValue(ClaimTypes.Email);

        // Supervisores y admins ven y mueven el trabajo de todo el equipo.
        public bool IsSupervisor => user.IsInRole(HelpDeskRoles.Supervisor) || user.IsInRole(HelpDeskRoles.Admin);

        // Cómo firma lo que escribe: su nombre de agente, o la parte de su email antes de la @ ("laura").
        public string AuthorName => user.Agent?.Value ?? user.Identity?.Name?.Split('@')[0] ?? "desconocido";

        public bool IsAgent(AgentName? name) => user.Agent is { } agent && agent == name;
    }
}
