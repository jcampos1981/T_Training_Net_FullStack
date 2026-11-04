using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Portal;

public static class ListMyTicketComments
{
    public sealed record Query(TicketId Id, ClaimsPrincipal User) : IRequest<Result<IReadOnlyList<CommentResponse>>>;

    public sealed class Handler(PortalTicketAccess access, CommentQueries queries) : IRequestHandler<Query, Result<IReadOnlyList<CommentResponse>>>
    {
        public async Task<Result<IReadOnlyList<CommentResponse>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            Result<Ticket> ticket = await access.FindMineAsync(query.Id, query.User, cancellationToken);

            return ticket.IsSuccess
                ? Result<IReadOnlyList<CommentResponse>>.Success(await queries.ListForTicketAsync(ticket.Value.Id, cancellationToken))
                : ticket.Error;
        }
    }

    public static void Map(IEndpointRouteBuilder portal) =>
        portal.MapGet("/{id:int:min(1)}/comments", HandleAsync)
            .WithName(nameof(ListMyTicketComments))
            .WithSummary("La conversación de uno de mis tickets, del mensaje más reciente al más antiguo.")
            .Produces<IReadOnlyList<CommentResponse>>()
            .Produces(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(int id, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Query(new TicketId(id), user), cancellationToken)).ToHttp(comments => TypedResults.Ok(comments));
}
