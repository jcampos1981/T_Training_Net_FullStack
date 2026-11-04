using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Authorization;

namespace HelpDesk.Api.Features.Tickets;

public static class UpdateTicket
{
    public sealed record Command(TicketId Id, UpdateTicketRequest Data, ClaimsPrincipal User) : IRequest<Result<TicketResponse>>;

    public sealed class Handler(
        TicketAccess access,
        ITicketRepository repository,
        IAuthorizationService authorization,
        ITicketNotifier notifier,
        ISlaPolicy sla) : IRequestHandler<Command, Result<TicketResponse>>
    {
        public async Task<Result<TicketResponse>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            (TicketId id, UpdateTicketRequest request, ClaimsPrincipal user) = command;

            // Se autoriza contra el ticket TAL COMO ESTÁ en la base, no contra lo que manda el cliente: si no, bastaría con poner
            // "assignedTo": "pedro" en el cuerpo para editar el ticket de otro.
            Result<Ticket> found = await access.FindAsync(id, user, TicketOperations.Update, cancellationToken);
            if (!found.IsSuccess)
            {
                return found.Error;
            }

            Ticket current = found.Value;

            AgentName? assignedTo = AgentName.From(request.AssignedTo);
            if (current.AssignedTo != assignedTo && !(await authorization.AuthorizeAsync(user, current, TicketOperations.Reassign)).Succeeded)
            {
                return Error.Forbidden;
            }

            Result<Ticket> changed = current.Update(request.Title, request.Priority, request.Status, assignedTo);
            if (!changed.IsSuccess)
            {
                return changed.Error;
            }

            // Antes de guardar: UpdateAsync adjunta "current" a EF, que le escribe los valores nuevos aunque sea un record
            // inmutable. Después de esa línea, "current" ya no dice cómo estaba el ticket.
            TicketEventKind kind = KindOfChange(current, changed.Value);
            AgentName? previousAgent = current.AssignedTo;

            Result<Ticket> saved = await repository.UpdateAsync(current, changed.Value, request.ExpectedVersion(), cancellationToken);
            if (!saved.IsSuccess)
            {
                return saved.Error;
            }

            await notifier.TicketChangedAsync(saved.Value, kind, user, previousAgent);

            return saved.Value.ToResponse(sla);
        }

        // "Reasignado" solo si no cambió nada más: al cliente no se le avisa de eso, pero sí de un cambio de estado.
        private static TicketEventKind KindOfChange(Ticket before, Ticket after)
        {
            bool visibleToCustomer = before.Title != after.Title || before.Priority != after.Priority || before.Status != after.Status;

            return !visibleToCustomer && before.AssignedTo != after.AssignedTo ? TicketEventKind.Reassigned : TicketEventKind.Updated;
        }
    }

    public static void Map(IEndpointRouteBuilder tickets) =>
        tickets.MapPut("/{id:int:min(1)}", HandleAsync)
            .WithName(nameof(UpdateTicket))
            .WithSummary("Reemplaza los datos de un ticket. Un ticket cerrado no se puede modificar; un agente solo edita los suyos y no puede reasignarlos.")
            .Produces<TicketResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> HandleAsync(int id, UpdateTicketRequest request, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Command(new TicketId(id), request, user), cancellationToken)).ToHttp(ticket => TypedResults.Ok(ticket));
}
