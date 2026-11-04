using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

public static class GetTicket
{
    public sealed record Query(TicketId Id, ClaimsPrincipal User) : IRequest<Result<TicketResponse>>;

    public sealed class Handler(TicketAccess access, ISlaPolicy sla) : IRequestHandler<Query, Result<TicketResponse>>
    {
        public async Task<Result<TicketResponse>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            Result<Ticket> ticket = await access.FindAsync(query.Id, query.User, TicketOperations.Read, cancellationToken);

            return ticket.IsSuccess ? ticket.Value.ToResponse(sla) : ticket.Error;
        }
    }

    public static void Map(IEndpointRouteBuilder tickets) =>
        tickets.MapGet("/{id:int:min(1)}", HandleAsync)
            .WithName(nameof(GetTicket))
            .WithSummary("Obtiene un ticket por su Id. Un agente solo puede ver los suyos.")
            .Produces<TicketResponse>()
            .Produces(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(int id, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Query(new TicketId(id), user), cancellationToken)).ToHttp(ticket => TypedResults.Ok(ticket));
}
