using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Comments;

public static class ListComments
{
    public sealed record Query(TicketId TicketId, ClaimsPrincipal User) : IRequest<Result<IReadOnlyList<CommentResponse>>>;

    public sealed class Handler(TicketAccess access, CommentQueries queries) : IRequestHandler<Query, Result<IReadOnlyList<CommentResponse>>>
    {
        public async Task<Result<IReadOnlyList<CommentResponse>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            Result<Ticket> ticket = await access.FindAsync(query.TicketId, query.User, TicketOperations.Read, cancellationToken);

            return ticket.IsSuccess
                ? Result<IReadOnlyList<CommentResponse>>.Success(await queries.ListForTicketAsync(query.TicketId, cancellationToken))
                : ticket.Error;
        }
    }

    public static void Map(IEndpointRouteBuilder comments) =>
        comments.MapGet("/", HandleAsync)
            .WithName(nameof(ListComments))
            .WithSummary("Lista los comentarios de un ticket, del más reciente al más antiguo.")
            .Produces<IReadOnlyList<CommentResponse>>()
            .Produces(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(int ticketId, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Query(new TicketId(ticketId), user), cancellationToken)).ToHttp(comments => TypedResults.Ok(comments));
}
