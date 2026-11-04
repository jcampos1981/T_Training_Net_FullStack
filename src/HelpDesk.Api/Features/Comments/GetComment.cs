using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Comments;

public static class GetComment
{
    public sealed record Query(TicketId TicketId, int CommentId, ClaimsPrincipal User) : IRequest<Result<CommentResponse>>;

    public sealed class Handler(TicketAccess access, CommentQueries queries) : IRequestHandler<Query, Result<CommentResponse>>
    {
        public async Task<Result<CommentResponse>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            Result<Ticket> ticket = await access.FindAsync(query.TicketId, query.User, TicketOperations.Read, cancellationToken);
            if (!ticket.IsSuccess)
            {
                return ticket.Error;
            }

            CommentResponse? comment = await queries.FindAsync(query.TicketId, query.CommentId, cancellationToken);

            return comment is null ? Error.NotFound : comment;
        }
    }

    public static void Map(IEndpointRouteBuilder comments) =>
        comments.MapGet("/{commentId:int:min(1)}", HandleAsync)
            .WithName(nameof(GetComment))
            .WithSummary("Obtiene un comentario de un ticket.")
            .Produces<CommentResponse>()
            .Produces(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(int ticketId, int commentId, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Query(new TicketId(ticketId), commentId, user), cancellationToken)).ToHttp(comment => TypedResults.Ok(comment));
}
