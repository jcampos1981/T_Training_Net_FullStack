using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

public static class DeleteTicket
{
    public sealed record Command(TicketId Id, ClaimsPrincipal User) : IRequest<Result<Unit>>;

    public sealed class Handler(ITicketRepository repository, ITicketNotifier notifier) : IRequestHandler<Command, Result<Unit>>
    {
        public async Task<Result<Unit>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            // Se lee antes de borrarlo: después ya no se sabría a quién avisarle.
            Ticket? ticket = await repository.FindAsync(command.Id, cancellationToken);

            if (ticket is null || !await repository.DeleteAsync(ticket.Id, cancellationToken))
            {
                return Error.NotFound;
            }

            await notifier.TicketChangedAsync(ticket, TicketEventKind.Deleted, command.User);

            return new Unit();
        }
    }

    public static void Map(IEndpointRouteBuilder tickets) =>
        tickets.MapDelete("/{id:int:min(1)}", HandleAsync)
            .WithName(nameof(DeleteTicket))
            .WithSummary("Elimina un ticket. Solo supervisión.")
            .RequireAuthorization(HelpDeskPolicies.Supervision)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(int id, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Command(new TicketId(id), user), cancellationToken)).ToHttp(_ => TypedResults.NoContent());
}
