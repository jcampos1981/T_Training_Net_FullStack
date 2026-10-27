using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace HelpDesk.Api.Features.Tickets;

// Autorización por recurso: la decisión depende del ticket, no solo del rol. Un [Authorize] no alcanza, porque
// se evalúa ANTES de cargar el ticket: el endpoint lo carga y llama a IAuthorizationService.AuthorizeAsync.
// Todas las reglas de acceso a tickets viven aquí:
// - Supervisor y Admin: todo.
// - Agente: leer, editar y comentar SUS tickets. Nunca reasignar (pasarle un ticket a otro es decisión del supervisor).
public sealed class TicketAuthorizationHandler : AuthorizationHandler<OperationAuthorizationRequirement, Ticket>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OperationAuthorizationRequirement operation,
        Ticket ticket)
    {
        // Sin Succeed queda denegado. No se llama a Fail: así otro handler todavía podría permitirlo.
        if (IsAllowed(context.User, operation, ticket))
        {
            context.Succeed(operation);
        }

        return Task.CompletedTask;
    }

    // Qué puede listar el usuario: un supervisor, lo que pida; un agente, solo lo suyo (si no filtra por agente,
    // se le filtra solo). null = pidió los tickets de otro agente, o es un "agente" sin nombre de agente → 403.
    public static TicketFilter? ScopeListFilter(ClaimsPrincipal user, TicketFilter requested)
    {
        if (user.IsSupervisor)
        {
            return requested;
        }

        if (user.AgentName is not { } agent || (requested.AssignedTo is not null && !user.IsAgent(requested.AssignedTo)))
        {
            return null;
        }

        return requested with { AssignedTo = agent };
    }

    private static bool IsAllowed(ClaimsPrincipal user, OperationAuthorizationRequirement operation, Ticket ticket) =>
        user.IsSupervisor
        || (operation != TicketOperations.Reassign && user.IsAgent(ticket.AssignedTo));
}
