using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Portal;

public static class GetMyTicket
{
    public sealed record Query(TicketId Id, ClaimsPrincipal User) : IRequest<Result<PortalTicketResponse>>;

    public sealed class Handler(PortalTicketAccess access) : IRequestHandler<Query, Result<PortalTicketResponse>>
    {
        public async Task<Result<PortalTicketResponse>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            Result<Ticket> ticket = await access.FindMineAsync(query.Id, query.User, cancellationToken);

            return ticket.IsSuccess ? PortalTicketResponse.From(ticket.Value) : ticket.Error;
        }
    }

    public static void Map(IEndpointRouteBuilder portal) =>
        portal.MapGet("/{id:int:min(1)}", HandleAsync)
            .WithName(nameof(GetMyTicket))
            .WithSummary("Uno de mis tickets. Si no existe o es de otro cliente, 404.")
            .Produces<PortalTicketResponse>()
            .Produces(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(int id, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Query(new TicketId(id), user), cancellationToken)).ToHttp(ticket => TypedResults.Ok(ticket));
}
