using HelpDesk.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Api.Features.Tickets;

// Todo lo de "tickets" vive aquí: registro de servicios, rutas y handlers.
public static class TicketsFeature
{
    public static IServiceCollection AddTicketsFeature(this IServiceCollection services)
    {
        services.AddSingleton<ITicketRepository>(provider =>
            new InMemoryTicketRepository(TicketSeed.Create(provider.GetRequiredService<TimeProvider>().GetUtcNow())));

        return services;
    }

    public static IEndpointRouteBuilder MapTicketsFeature(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder tickets = app.MapGroup("/api/tickets").WithTags("Tickets");

        tickets.MapGet("/", ListTickets)
            .WithName(nameof(ListTickets))
            .WithSummary("Lista tickets, con filtros opcionales por estado, prioridad y agente.");

        tickets.MapGet("/{id:int:min(1)}", GetTicket)
            .WithName(nameof(GetTicket))
            .WithSummary("Obtiene un ticket por su Id.");

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

    private static async Task<Ok<TicketResponse[]>> ListTickets(
        [AsParameters] TicketFilter filter,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Ticket> tickets = await repository.ListAsync(filter, cancellationToken);
        return TypedResults.Ok(tickets.Select(ticket => ticket.ToResponse()).ToArray());
    }

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

        // Si el ticket está cerrado, Update lanza DomainRuleException y el manejador global responde 409.
        Ticket updated = current.Update(request.Title, request.Priority, request.Status, request.AssignedTo);
        await repository.UpdateAsync(updated, cancellationToken);

        return TypedResults.Ok(updated.ToResponse());
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
