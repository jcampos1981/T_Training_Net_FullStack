using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace HelpDesk.Api.Features.Tickets;

// Lo que se puede hacer con UN ticket. La respuesta depende del usuario Y del ticket: lo decide TicketAuthorizationHandler.
public static class TicketOperations
{
    public static readonly OperationAuthorizationRequirement Read = new() { Name = nameof(Read) };

    // Editar sus datos o agregarle un comentario.
    public static readonly OperationAuthorizationRequirement Update = new() { Name = nameof(Update) };

    // Cambiar a quién está asignado (al editarlo o al crearlo asignado a otro).
    public static readonly OperationAuthorizationRequirement Reassign = new() { Name = nameof(Reassign) };
}
