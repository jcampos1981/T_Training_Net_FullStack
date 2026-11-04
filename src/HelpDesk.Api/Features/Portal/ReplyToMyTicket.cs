using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Portal;

public static class ReplyToMyTicket
{
    public sealed record Command(TicketId Id, AddCommentRequest Data, ClaimsPrincipal User) : IRequest<Result<CommentResponse>>;

    public sealed class Handler(
        PortalTicketAccess access,
        ICommentRepository comments,
        ITicketNotifier notifier,
        TimeProvider clock) : IRequestHandler<Command, Result<CommentResponse>>
    {
        public async Task<Result<CommentResponse>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            (TicketId id, AddCommentRequest request, ClaimsPrincipal user) = command;

            Result<Ticket> ticket = await access.FindMineAsync(id, user, cancellationToken);
            if (!ticket.IsSuccess)
            {
                return ticket.Error;
            }

            // La misma regla que para el equipo: un ticket cerrado no admite comentarios.
            Result<TicketComment> comment = ticket.Value.CreateCustomerComment(user.AuthorName, request.Body, clock.GetUtcNow());
            if (!comment.IsSuccess)
            {
                return comment.Error;
            }

            TicketComment saved = await comments.AddAsync(comment.Value, cancellationToken);
            await notifier.TicketChangedAsync(ticket.Value, TicketEventKind.Commented, user);

            return CommentResponse.From(saved);
        }
    }

    public static void Map(IEndpointRouteBuilder portal) =>
        portal.MapPost("/{id:int:min(1)}/comments", HandleAsync)
            .WithName(nameof(ReplyToMyTicket))
            .WithSummary("Responde en uno de mis tickets. Un ticket cerrado no admite comentarios.")
            .Produces<CommentResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> HandleAsync(int id, AddCommentRequest request, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Command(new TicketId(id), request, user), cancellationToken))
            .ToHttp(comment => TypedResults.Created($"/api/portal/tickets/{id}/comments", comment));
}
