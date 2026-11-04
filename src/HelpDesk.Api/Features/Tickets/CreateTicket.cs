using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Authorization;

namespace HelpDesk.Api.Features.Tickets;

public static class CreateTicket
{
    public sealed record Command(CreateTicketRequest Data, ClaimsPrincipal User) : IRequest<Result<TicketResponse>>;

    public sealed class Handler(
        ITicketRepository repository,
        IAuthorizationService authorization,
        ITicketNotifier notifier,
        ISlaPolicy sla,
        TimeProvider clock) : IRequestHandler<Command, Result<TicketResponse>>
    {
        public async Task<Result<TicketResponse>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            (CreateTicketRequest request, ClaimsPrincipal user) = command;

            var ticket = new Ticket
            {
                Id = await repository.NextIdAsync(cancellationToken),
                Title = request.Title,
                Priority = request.Priority,
                CreatedAt = clock.GetUtcNow(),
                AssignedTo = AgentName.From(request.AssignedTo)
            };

            // Crearlo asignado a OTRO es repartir trabajo: la misma regla que reasignar.
            bool assignedToSomeoneElse = ticket.AssignedTo is not null && !user.IsAgent(ticket.AssignedTo);
            if (assignedToSomeoneElse && !(await authorization.AuthorizeAsync(user, ticket, TicketOperations.Reassign)).Succeeded)
            {
                return Error.Forbidden;
            }

            await repository.AddAsync(ticket, cancellationToken);
            await notifier.TicketChangedAsync(ticket, TicketEventKind.Created, user);

            return ticket.ToResponse(sla);
        }
    }

    public static void Map(IEndpointRouteBuilder tickets) =>
        tickets.MapPost("/", HandleAsync)
            .WithName(nameof(CreateTicket))
            .WithSummary("Crea un ticket abierto. Un agente solo puede dejarlo sin asignar o asignárselo a sí mismo.")
            .Produces<TicketResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

    // La validación de CreateTicketRequest ocurre antes de llegar aquí: si falla, responde 400 sola.
    private static async Task<IResult> HandleAsync(CreateTicketRequest request, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Command(request, user), cancellationToken))
            // 201 Created + cabecera Location apuntando a GET /api/tickets/{id}
            .ToHttp(ticket => TypedResults.CreatedAtRoute(ticket, nameof(GetTicket), new { id = ticket.Id }));
}
