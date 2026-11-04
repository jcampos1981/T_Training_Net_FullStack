using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Comments;

public static class AddComment
{
    public sealed record Command(TicketId TicketId, AddCommentRequest Data, ClaimsPrincipal User) : IRequest<Result<CommentResponse>>;

    public sealed class Handler(
        TicketAccess access,
        ICommentRepository comments,
        ITicketNotifier notifier,
        TimeProvider clock) : IRequestHandler<Command, Result<CommentResponse>>
    {
        public async Task<Result<CommentResponse>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            (TicketId ticketId, AddCommentRequest request, ClaimsPrincipal user) = command;

            // Quien puede editar un ticket puede comentarlo.
            Result<Ticket> ticket = await access.FindAsync(ticketId, user, TicketOperations.Update, cancellationToken);
            if (!ticket.IsSuccess)
            {
                return ticket.Error;
            }

            Result<TicketComment> comment = ticket.Value.CreateComment(user.AuthorName, request.Body, clock.GetUtcNow());
            if (!comment.IsSuccess)
            {
                return comment.Error;
            }

            TicketComment saved = await comments.AddAsync(comment.Value, cancellationToken);
            await notifier.TicketChangedAsync(ticket.Value, TicketEventKind.Commented, user);

            return CommentResponse.From(saved);
        }
    }

    public static void Map(IEndpointRouteBuilder comments) =>
        comments.MapPost("/", HandleAsync)
            .WithName(nameof(AddComment))
            .WithSummary("Agrega un comentario firmado por el usuario actual. Un ticket cerrado no admite comentarios.")
            .Produces<CommentResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> HandleAsync(int ticketId, AddCommentRequest request, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Command(new TicketId(ticketId), request, user), cancellationToken))
            .ToHttp(comment => TypedResults.CreatedAtRoute(comment, nameof(GetComment), new { ticketId, commentId = comment.Id }));
}
