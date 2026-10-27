using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Api.Features.Comments;

// Recurso anidado: los comentarios siempre se piden a través de su ticket, y heredan sus permisos:
// quien puede leer el ticket puede leer sus comentarios; quien puede editarlo, puede comentarlo.
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
        RouteGroupBuilder comments = app.MapGroup("/api/tickets/{ticketId:int:min(1)}/comments")
            .WithTags("Comments")
            .RequireAuthorization(HelpDeskPolicies.Staff)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        comments.MapGet("/", ListComments)
            .WithName(nameof(ListComments))
            .WithSummary("Lista los comentarios de un ticket, del más reciente al más antiguo.");

        comments.MapGet("/{commentId:int:min(1)}", GetComment)
            .WithName(nameof(GetComment))
            .WithSummary("Obtiene un comentario de un ticket.");

        comments.MapPost("/", AddComment)
            .WithName(nameof(AddComment))
            .WithSummary("Agrega un comentario firmado por el usuario actual. Un ticket cerrado no admite comentarios.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<CommentResponse>>, NotFound, ForbidHttpResult>> ListComments(
        int ticketId,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        ITicketRepository tickets,
        CommentQueries queries,
        CancellationToken cancellationToken)
    {
        var id = new TicketId(ticketId);
        Ticket? ticket = await tickets.FindAsync(id, cancellationToken);

        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        if (!(await authorization.AuthorizeAsync(user, ticket, TicketOperations.Read)).Succeeded)
        {
            return TypedResults.Forbid();
        }

        return TypedResults.Ok(await queries.ListForTicketAsync(id, cancellationToken));
    }

    private static async Task<Results<Ok<CommentResponse>, NotFound, ForbidHttpResult>> GetComment(
        int ticketId,
        int commentId,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        ITicketRepository tickets,
        CommentQueries queries,
        CancellationToken cancellationToken)
    {
        var id = new TicketId(ticketId);
        Ticket? ticket = await tickets.FindAsync(id, cancellationToken);

        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        if (!(await authorization.AuthorizeAsync(user, ticket, TicketOperations.Read)).Succeeded)
        {
            return TypedResults.Forbid();
        }

        CommentResponse? comment = await queries.FindAsync(id, commentId, cancellationToken);

        return comment is null ? TypedResults.NotFound() : TypedResults.Ok(comment);
    }

    private static async Task<Results<CreatedAtRoute<CommentResponse>, NotFound, ForbidHttpResult>> AddComment(
        int ticketId,
        AddCommentRequest request,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
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

        if (!(await authorization.AuthorizeAsync(user, ticket, TicketOperations.Update)).Succeeded)
        {
            return TypedResults.Forbid();
        }

        // Si el ticket está cerrado, CreateComment lanza DomainRuleException → 409.
        TicketComment comment = ticket.CreateComment(user.AuthorName, request.Body, clock.GetUtcNow());
        TicketComment saved = await comments.AddAsync(comment, cancellationToken);

        return TypedResults.CreatedAtRoute(
            CommentResponse.From(saved),
            nameof(GetComment),
            new { ticketId, commentId = saved.Id });
    }
}
