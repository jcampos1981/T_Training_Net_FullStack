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
}
