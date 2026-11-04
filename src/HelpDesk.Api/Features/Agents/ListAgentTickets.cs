using System.Security.Claims;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Mediation;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Agents;

public static class ListAgentTickets
{
    public sealed record Query(string Agent, PageRequest Page, ClaimsPrincipal User) : IRequest<Result<CursorPage<TicketResponse>>>;

    public sealed class Handler(ITicketRepository repository, ISlaPolicy sla) : IRequestHandler<Query, Result<CursorPage<TicketResponse>>>
    {
        public async Task<Result<CursorPage<TicketResponse>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            // La misma regla que GET /api/tickets: el agente de la ruta tiene que ser él mismo, salvo para supervisión.
            var requested = new TicketFilter(Status: null, Priority: null, AssignedTo: query.Agent);
            if (TicketAuthorizationHandler.ScopeListFilter(query.User, requested) is not { } allowed)
            {
                return Error.Forbidden;
            }

            IReadOnlyList<Ticket> tickets = await repository.ListAsync(allowed, query.Page.AfterTicket(), query.Page.Take(), cancellationToken);

            return CursorPage.Create(tickets, query.Page, ticket => ticket.ToResponse(sla), ticket => ticket.Id.Value);
        }
    }
}
