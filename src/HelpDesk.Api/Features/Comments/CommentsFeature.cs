using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Api.Features.Comments;

// Recurso anidado: los comentarios siempre se piden a través de su ticket.
public static class CommentsFeature
{
    public static IServiceCollection AddCommentsFeature(this IServiceCollection services)
    {
        services.AddScoped<ICommentRepository, EfCommentRepository>();
        services.AddScoped<CommentQueries>();
        return services;
    }

    public static IEndpointRouteBuilder MapCommentsFeature(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder comments = app.MapGroup("/api/tickets/{ticketId:int:min(1)}/comments").WithTags("Comments");

        comments.MapGet("/", ListComments)
            .WithName(nameof(ListComments))
            .WithSummary("Lista los comentarios de un ticket, del más reciente al más antiguo.");

        comments.MapGet("/{commentId:int:min(1)}", GetComment)
            .WithName(nameof(GetComment))
            .WithSummary("Obtiene un comentario de un ticket.");

        comments.MapPost("/", AddComment)
            .WithName(nameof(AddComment))
            .WithSummary("Agrega un comentario. Un ticket cerrado no admite comentarios.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<CommentResponse>>, NotFound>> ListComments(
        int ticketId,
        CommentQueries queries,
        CancellationToken cancellationToken)
    {
        var id = new TicketId(ticketId);

        if (!await queries.TicketExistsAsync(id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await queries.ListForTicketAsync(id, cancellationToken));
    }

    private static async Task<Results<Ok<CommentResponse>, NotFound>> GetComment(
        int ticketId,
        int commentId,
        CommentQueries queries,
        CancellationToken cancellationToken)
    {
        CommentResponse? comment = await queries.FindAsync(new TicketId(ticketId), commentId, cancellationToken);

        return comment is null ? TypedResults.NotFound() : TypedResults.Ok(comment);
    }

    private static async Task<Results<CreatedAtRoute<CommentResponse>, NotFound>> AddComment(
        int ticketId,
        AddCommentRequest request,
        ITicketRepository tickets,
        ICommentRepository comments,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        Ticket? ticket = await tickets.FindAsync(new TicketId(ticketId), cancellationToken);

        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        // Si el ticket está cerrado, CreateComment lanza DomainRuleException → 409.
        TicketComment comment = ticket.CreateComment(request.Author, request.Body, clock.GetUtcNow());
        TicketComment saved = await comments.AddAsync(comment, cancellationToken);

        return TypedResults.CreatedAtRoute(
            CommentResponse.From(saved),
            nameof(GetComment),
            new { ticketId, commentId = saved.Id });
    }
}
