using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Api.Features.Portal;

// El portal del cliente: abrir tickets, seguir los propios y conversar en ellos. Otra audiencia que /api/tickets,
// con otras reglas y otro contrato; por eso es otra feature y no un "if (esCliente)" dentro de la de soporte.
public static class PortalFeature
{
    public static IServiceCollection AddPortalFeature(this IServiceCollection services)
    {
        services.AddScoped<OpenTicketHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapPortalFeature(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder portal = app.MapGroup("/api/portal/tickets")
            .WithTags("Portal del cliente")
            .RequireAuthorization(HelpDeskPolicies.Customer)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        portal.MapGet("/", ListMyTickets)
            .WithName(nameof(ListMyTickets))
            .WithSummary("Mis tickets, por páginas (keyset: ?after=&pageSize=).")
            .ProducesValidationProblem();

        portal.MapGet("/{id:int:min(1)}", GetMyTicket)
            .WithName(nameof(GetMyTicket))
            .WithSummary("Uno de mis tickets. Si no existe o es de otro cliente, 404.");

        portal.MapPost("/", OpenTicket)
            .WithName(nameof(OpenTicket))
            .WithSummary("Abre un ticket. La descripción queda como su primer comentario.")
            .ProducesValidationProblem();

        portal.MapGet("/{id:int:min(1)}/comments", ListMyTicketComments)
            .WithName(nameof(ListMyTicketComments))
            .WithSummary("La conversación de uno de mis tickets, del mensaje más reciente al más antiguo.");

        portal.MapPost("/{id:int:min(1)}/comments", ReplyToMyTicket)
            .WithName(nameof(ReplyToMyTicket))
            .WithSummary("Responde en uno de mis tickets. Un ticket cerrado no admite comentarios.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Ok<CursorPage<PortalTicketResponse>>> ListMyTickets(
        [AsParameters] PageRequest page,
        ClaimsPrincipal user,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        // El filtro por cliente no es opcional ni viene del query string: sale del token. Usa IX_Tickets_RequestedBy.
        var mine = new TicketFilter(Status: null, Priority: null, AssignedTo: null, RequestedBy: CustomerEmail(user));
        IReadOnlyList<Ticket> tickets = await repository.ListAsync(mine, TicketsFeature.AfterTicket(page), page.Take(), cancellationToken);

        return TypedResults.Ok(CursorPage.Create(tickets, page, PortalTicketResponse.From, ticket => ticket.Id.Value));
    }

    private static async Task<Results<Ok<PortalTicketResponse>, NotFound>> GetMyTicket(
        int id,
        ClaimsPrincipal user,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        Ticket? ticket = await FindMineAsync(id, user, repository, cancellationToken);

        return ticket is null ? TypedResults.NotFound() : TypedResults.Ok(PortalTicketResponse.From(ticket));
    }

    private static async Task<CreatedAtRoute<PortalTicketResponse>> OpenTicket(
        OpenTicketRequest request,
        ClaimsPrincipal user,
        OpenTicketHandler handler,
        CancellationToken cancellationToken)
    {
        Ticket ticket = await handler.HandleAsync(request, CustomerEmail(user), user.AuthorName, cancellationToken);

        return TypedResults.CreatedAtRoute(PortalTicketResponse.From(ticket), nameof(GetMyTicket), new { id = ticket.Id.Value });
    }

    private static async Task<Results<Ok<IReadOnlyList<CommentResponse>>, NotFound>> ListMyTicketComments(
        int id,
        ClaimsPrincipal user,
        ITicketRepository repository,
        CommentQueries queries,
        CancellationToken cancellationToken)
    {
        Ticket? ticket = await FindMineAsync(id, user, repository, cancellationToken);

        return ticket is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(await queries.ListForTicketAsync(ticket.Id, cancellationToken));
    }

    private static async Task<Results<Created<CommentResponse>, NotFound>> ReplyToMyTicket(
        int id,
        AddCommentRequest request,
        ClaimsPrincipal user,
        ITicketRepository repository,
        ICommentRepository comments,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        Ticket? ticket = await FindMineAsync(id, user, repository, cancellationToken);
        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        // Si el ticket está cerrado, CreateComment lanza DomainRuleException → 409, igual que para el equipo.
        TicketComment saved = await comments.AddAsync(
            ticket.CreateComment(user.AuthorName, request.Body, clock.GetUtcNow()), cancellationToken);

        return TypedResults.Created($"/api/portal/tickets/{id}/comments", CommentResponse.From(saved));
    }

    // 404 y no 403 cuando el ticket es de otro cliente: un 403 confirmaría que ese Id existe, y como los Id son
    // correlativos, un cliente podría recorrerlos para saber cuántos tickets tiene la empresa (y de qué clientes).
    // Para quien pregunta, un ticket ajeno y uno inexistente son lo mismo.
    private static async Task<Ticket?> FindMineAsync(
        int id,
        ClaimsPrincipal user,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        Ticket? ticket = await repository.FindAsync(new TicketId(id), cancellationToken);

        return ticket is not null && string.Equals(ticket.RequestedBy, CustomerEmail(user), StringComparison.OrdinalIgnoreCase)
            ? ticket
            : null;
    }

    // La policy Customer exige el claim de email, así que aquí siempre está.
    private static string CustomerEmail(ClaimsPrincipal user) =>
        user.Email ?? throw new InvalidOperationException("La policy Customer garantiza el claim de email.");
}
