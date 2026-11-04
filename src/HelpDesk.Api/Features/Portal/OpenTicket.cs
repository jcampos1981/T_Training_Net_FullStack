using System.Security.Claims;
using HelpDesk.Api.Data;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Portal;

public static class OpenTicket
{
    public sealed record Command(OpenTicketRequest Data, ClaimsPrincipal User) : IRequest<Result<PortalTicketResponse>>;

    public sealed class Handler(
        HelpDeskDbContext db,
        ITicketRepository tickets,
        ITicketNotifier notifier,
        TimeProvider clock) : IRequestHandler<Command, Result<PortalTicketResponse>>
    {
        public async Task<Result<PortalTicketResponse>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            (OpenTicketRequest request, ClaimsPrincipal user) = command;

            Ticket ticket = await SaveAsync(request, PortalTicketAccess.EmailOf(user), user.AuthorName, cancellationToken);
            await notifier.TicketChangedAsync(ticket, TicketEventKind.Created, user);

            return PortalTicketResponse.From(ticket);
        }

        // El ticket y su descripción (primer comentario) se guardan juntos. Público para que los datos de ejemplo pasen por aquí.
        public async Task<Ticket> SaveAsync(OpenTicketRequest request, string customerEmail, string author, CancellationToken cancellationToken)
        {
            DateTimeOffset now = clock.GetUtcNow();

            var ticket = new Ticket
            {
                // El Id se reserva antes (SEQUENCE): así el comentario puede apuntar al ticket antes de guardar nada.
                Id = await tickets.NextIdAsync(cancellationToken),
                Title = request.Title,
                Priority = request.Priority,
                CreatedAt = now,
                RequestedBy = customerEmail
            };

            // Un ticket recién creado siempre admite comentarios: si esto falla, es un bug y no un resultado posible.
            TicketComment description = ticket.CreateCustomerComment(author, request.Description, now).Value;

            // Un solo SaveChanges = una sola transacción: se guardan los dos o ninguno. No hace falta abrir una a
            // mano (como en la reasignación masiva), porque aquí no hay dos sentencias separadas que coordinar.
            db.Tickets.Add(ticket);
            db.Comments.Add(description);
            await db.SaveChangesAsync(cancellationToken);

            return ticket;
        }
    }

    public static void Map(IEndpointRouteBuilder portal) =>
        portal.MapPost("/", HandleAsync)
            .WithName(nameof(OpenTicket))
            .WithSummary("Abre un ticket. La descripción queda como su primer comentario.")
            .Produces<PortalTicketResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

    private static async Task<IResult> HandleAsync(OpenTicketRequest request, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Command(request, user), cancellationToken))
            .ToHttp(ticket => TypedResults.CreatedAtRoute(ticket, nameof(GetMyTicket), new { id = ticket.Id }));
}
