using System.Security.Claims;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Api.Features.Portal;

// Una lectura que no puede fallar: el endpoint le pregunta directo al repositorio.
public static class ListMyTickets
{
    public static void Map(IEndpointRouteBuilder portal) =>
        portal.MapGet("/", HandleAsync)
            .WithName(nameof(ListMyTickets))
            .WithSummary("Mis tickets, por páginas (keyset: ?after=&pageSize=).")
            .ProducesValidationProblem();

    private static async Task<Ok<CursorPage<PortalTicketResponse>>> HandleAsync(
        [AsParameters] PageRequest page,
        ClaimsPrincipal user,
        ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        // El filtro por cliente no es opcional ni viene del query string: sale del token. Usa IX_Tickets_RequestedBy.
        var mine = new TicketFilter(Status: null, Priority: null, AssignedTo: null, RequestedBy: PortalTicketAccess.EmailOf(user));
        IReadOnlyList<Ticket> tickets = await repository.ListAsync(mine, page.AfterTicket(), page.Take(), cancellationToken);

        return TypedResults.Ok(CursorPage.Create(tickets, page, PortalTicketResponse.From, ticket => ticket.Id.Value));
    }
}
