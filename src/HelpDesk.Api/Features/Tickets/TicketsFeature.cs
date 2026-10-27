using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Authorization;
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

        // Singleton: no guarda estado; la decisión sale del usuario y del ticket que recibe en cada llamada.
        services.AddSingleton<IAuthorizationHandler, TicketAuthorizationHandler>();

        return services;
    }

    public static IEndpointRouteBuilder MapTicketsFeature(this IEndpointRouteBuilder app)
    {
        // Solo el equipo de soporte. Lo fino (qué tickets) lo decide cada endpoint con TicketAuthorizationHandler.
        RouteGroupBuilder tickets = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(HelpDeskPolicies.Staff)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        tickets.MapGet("/", ListTickets)
            .WithName(nameof(ListTickets))
            .WithSummary("Lista tickets por páginas (keyset: ?after=&pageSize=), con filtros opcionales por estado, prioridad y agente. Un agente solo ve los suyos.")
            .ProducesValidationProblem();

        tickets.MapGet("/{id:int:min(1)}", GetTicket)
            .WithName(nameof(GetTicket))
            .WithSummary("Obtiene un ticket por su Id. Un agente solo puede ver los suyos.");

        tickets.MapGet("/activity", ListActivity)
            .WithName(nameof(ListActivity))
            .WithSummary("Tickets abiertos de todo el equipo, por páginas, con sus comentarios más recientes. Solo supervisión.")
            .RequireAuthorization(HelpDeskPolicies.Supervision)
            .ProducesValidationProblem();

        tickets.MapPost("/", CreateTicket)
            .WithName(nameof(CreateTicket))
            .WithSummary("Crea un ticket abierto. Un agente solo puede dejarlo sin asignar o asignárselo a sí mismo.")
            .ProducesValidationProblem();

        tickets.MapPut("/{id:int:min(1)}", UpdateTicket)
            .WithName(nameof(UpdateTicket))
            .WithSummary("Reemplaza los datos de un ticket. Un ticket cerrado no se puede modificar; un agente solo edita los suyos y no puede reasignarlos.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        tickets.MapDelete("/{id:int:min(1)}", DeleteTicket)
            .WithName(nameof(DeleteTicket))
            .WithSummary("Elimina un ticket. Solo supervisión.")
            .RequireAuthorization(HelpDeskPolicies.Supervision);

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
    private static async Task<Results<Ok<CursorPage<TicketResponse>>, ForbidHttpResult>> ListTickets(
        [AsParameters] TicketFilter filter,
        [AsParameters] PageRequest page,
        ClaimsPrincipal user,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        // Un agente solo lista lo suyo: el filtro se ajusta aquí, ANTES de consultar, no después de traer los datos.
        if (TicketAuthorizationHandler.ScopeListFilter(user, filter) is not { } allowed)
        {
            return TypedResults.Forbid();
        }

        IReadOnlyList<Ticket> tickets = await repository.ListAsync(allowed, AfterTicket(page), page.Take(), cancellationToken);

        return TypedResults.Ok(CursorPage.Create(tickets, page, ticket => ticket.ToResponse(), ticket => ticket.Id.Value));
    }

    // after=0 significa "desde el principio": no hay TicketId 0.
    internal static TicketId? AfterTicket(PageRequest page) => page.After > 0 ? new TicketId(page.After) : null;

    // 403 y no 404 para el ticket de otro agente: dentro del equipo, que un ticket exista no es un secreto (los Id
    // son correlativos). Para clientes (Fase 6) conviene 404: no deben poder averiguar qué tickets existen.
    private static async Task<Results<Ok<TicketResponse>, NotFound, ForbidHttpResult>> GetTicket(
        int id,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        Ticket? ticket = await repository.FindAsync(new TicketId(id), cancellationToken);

        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        if (!(await authorization.AuthorizeAsync(user, ticket, TicketOperations.Read)).Succeeded)
        {
            return TypedResults.Forbid();
        }

        return TypedResults.Ok(ticket.ToResponse());
    }

    // La validación de CreateTicketRequest ocurre antes de llegar aquí: si falla, responde 400 sola.
    private static async Task<Results<CreatedAtRoute<TicketResponse>, ForbidHttpResult>> CreateTicket(
        CreateTicketRequest request,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
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

        // Crearlo asignado a OTRO es repartir trabajo: la misma regla que reasignar.
        bool assignedToSomeoneElse = ticket.AssignedTo is not null && !user.IsAgent(ticket.AssignedTo);
        if (assignedToSomeoneElse && !(await authorization.AuthorizeAsync(user, ticket, TicketOperations.Reassign)).Succeeded)
        {
            return TypedResults.Forbid();
        }

        await repository.AddAsync(ticket, cancellationToken);

        // 201 Created + cabecera Location apuntando a GET /api/tickets/{id}
        return TypedResults.CreatedAtRoute(ticket.ToResponse(), nameof(GetTicket), new { id = ticket.Id.Value });
    }

    private static async Task<Results<Ok<TicketResponse>, NotFound, ForbidHttpResult>> UpdateTicket(
        int id,
        UpdateTicketRequest request,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        Ticket? current = await repository.FindAsync(new TicketId(id), cancellationToken);

        if (current is null)
        {
            return TypedResults.NotFound();
        }

        // Se autoriza contra el ticket TAL COMO ESTÁ en la base, no contra lo que manda el cliente: si no, bastaría
        // con poner "assignedTo": "pedro" en el cuerpo para editar el ticket de otro.
        if (!(await authorization.AuthorizeAsync(user, current, TicketOperations.Update)).Succeeded)
        {
            return TypedResults.Forbid();
        }

        bool reassigns = !string.Equals(current.AssignedTo, request.AssignedTo, StringComparison.OrdinalIgnoreCase);
        if (reassigns && !(await authorization.AuthorizeAsync(user, current, TicketOperations.Reassign)).Succeeded)
        {
            return TypedResults.Forbid();
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
