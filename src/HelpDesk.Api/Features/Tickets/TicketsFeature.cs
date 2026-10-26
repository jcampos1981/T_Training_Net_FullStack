using System.ComponentModel.DataAnnotations;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Api.Features.Tickets;

// Todo lo de "tickets" vive aquí: registro de servicios, rutas y handlers.
public static class TicketsFeature
{
    public static IServiceCollection AddTicketsFeature(this IServiceCollection services)
    {
        // Scoped: un repositorio por petición HTTP, igual que el DbContext que usa por dentro.
        services.AddScoped<ITicketRepository, EfTicketRepository>();
        services.AddScoped<TicketActivityQuery>();

        return services;
    }

    public static IEndpointRouteBuilder MapTicketsFeature(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder tickets = app.MapGroup("/api/tickets").WithTags("Tickets");

        tickets.MapGet("/", ListTickets)
            .WithName(nameof(ListTickets))
            .WithSummary("Lista tickets por páginas (keyset: ?after=&pageSize=), con filtros opcionales por estado, prioridad y agente.")
            .ProducesValidationProblem();

        tickets.MapGet("/{id:int:min(1)}", GetTicket)
            .WithName(nameof(GetTicket))
            .WithSummary("Obtiene un ticket por su Id.");

        tickets.MapGet("/activity", ListActivity)
            .WithName(nameof(ListActivity))
            .WithSummary("Tickets abiertos, por páginas, con su número de comentarios y los más recientes.")
            .ProducesValidationProblem();

        tickets.MapPost("/", CreateTicket)
            .WithName(nameof(CreateTicket))
            .WithSummary("Crea un ticket abierto.")
            .ProducesValidationProblem();

        tickets.MapPut("/{id:int:min(1)}", UpdateTicket)
            .WithName(nameof(UpdateTicket))
            .WithSummary("Reemplaza los datos de un ticket. Un ticket cerrado no se puede modificar.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        tickets.MapDelete("/{id:int:min(1)}", DeleteTicket)
            .WithName(nameof(DeleteTicket))
            .WithSummary("Elimina un ticket.");

        return app;
    }

    private static async Task<Ok<CursorPage<TicketActivityResponse>>> ListActivity(
        [AsParameters] PageRequest page,
        TicketActivityQuery query,
        CancellationToken cancellationToken,
        [Range(1, 10)] int latestComments = 3)
    {
        return TypedResults.Ok(await query.ListOpenAsync(page, latestComments, cancellationToken));
    }

    // Antes devolvía TODOS los tickets que cumplían el filtro (300 mil para pedro). Ahora, una página.
    private static async Task<Ok<CursorPage<TicketResponse>>> ListTickets(
        [AsParameters] TicketFilter filter,
        [AsParameters] PageRequest page,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Ticket> tickets = await repository.ListAsync(filter, AfterTicket(page), page.Take(), cancellationToken);

        return TypedResults.Ok(CursorPage.Create(tickets, page, ticket => ticket.ToResponse(), ticket => ticket.Id.Value));
    }

    // after=0 significa "desde el principio": no hay TicketId 0.
    internal static TicketId? AfterTicket(PageRequest page) => page.After > 0 ? new TicketId(page.After) : null;

    private static async Task<Results<Ok<TicketResponse>, NotFound>> GetTicket(
        int id,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        Ticket? ticket = await repository.FindAsync(new TicketId(id), cancellationToken);

        return ticket is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ticket.ToResponse());
    }

    // La validación de CreateTicketRequest ocurre antes de llegar aquí: si falla, responde 400 sola.
    private static async Task<CreatedAtRoute<TicketResponse>> CreateTicket(
        CreateTicketRequest request,
        ITicketRepository repository,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var ticket = new Ticket
        {
            Id = await repository.NextIdAsync(cancellationToken),
            Title = request.Title,
            Priority = request.Priority,
            CreatedAt = clock.GetUtcNow(),
            AssignedTo = request.AssignedTo
        };

        await repository.AddAsync(ticket, cancellationToken);

        // 201 Created + cabecera Location apuntando a GET /api/tickets/{id}
        return TypedResults.CreatedAtRoute(ticket.ToResponse(), nameof(GetTicket), new { id = ticket.Id.Value });
    }

    private static async Task<Results<Ok<TicketResponse>, NotFound>> UpdateTicket(
        int id,
        UpdateTicketRequest request,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        Ticket? current = await repository.FindAsync(new TicketId(id), cancellationToken);

        if (current is null)
        {
            return TypedResults.NotFound();
        }

        // Dos posibles 409, ambos traducidos por el manejador global de errores:
        // - el ticket está cerrado (regla de negocio, DomainRuleException)
        // - otro usuario lo guardó primero (concurrencia, DbUpdateConcurrencyException)
        Ticket changed = current.Update(request.Title, request.Priority, request.Status, request.AssignedTo);
        Ticket saved = await repository.UpdateAsync(current, changed, request.ExpectedVersion(), cancellationToken);

        return TypedResults.Ok(saved.ToResponse());
    }

    private static async Task<Results<NoContent, NotFound>> DeleteTicket(
        int id,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        bool deleted = await repository.DeleteAsync(new TicketId(id), cancellationToken);

        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
