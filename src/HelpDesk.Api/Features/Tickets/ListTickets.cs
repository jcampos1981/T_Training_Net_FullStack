using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Mediation;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

public static class ListTickets
{
    public sealed record Query(TicketFilter Filter, PageRequest Page, ClaimsPrincipal User) : IRequest<Result<CursorPage<TicketResponse>>>;

    public sealed class Handler(ITicketRepository repository, ISlaPolicy sla) : IRequestHandler<Query, Result<CursorPage<TicketResponse>>>
    {
        public async Task<Result<CursorPage<TicketResponse>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            // Un agente solo lista lo suyo: el filtro se ajusta aquí, ANTES de consultar, no después de traer los datos.
            if (TicketAuthorizationHandler.ScopeListFilter(query.User, query.Filter) is not { } allowed)
            {
                return Error.Forbidden;
            }

            // Una página, y no todos los que cumplan el filtro: pedro tiene 300 mil.
            IReadOnlyList<Ticket> tickets = await repository.ListAsync(allowed, query.Page.AfterTicket(), query.Page.Take(), cancellationToken);

            return CursorPage.Create(tickets, query.Page, ticket => ticket.ToResponse(sla), ticket => ticket.Id.Value);
        }
    }

    public static void Map(IEndpointRouteBuilder tickets) =>
        tickets.MapGet("/", HandleAsync)
            .WithName(nameof(ListTickets))
            .WithSummary("Lista tickets por páginas (keyset: ?after=&pageSize=), con filtros opcionales por estado, prioridad y agente. Un agente solo ve los suyos.")
            .Produces<CursorPage<TicketResponse>>()
            .ProducesValidationProblem();

    private static async Task<IResult> HandleAsync(
        [AsParameters] TicketFilter filter,
        [AsParameters] PageRequest page,
        ClaimsPrincipal user,
        ISender sender,
        CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Query(filter, page, user), cancellationToken)).ToHttp(tickets => TypedResults.Ok(tickets));
}
