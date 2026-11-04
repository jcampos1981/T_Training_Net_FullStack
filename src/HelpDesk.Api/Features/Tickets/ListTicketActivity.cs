using System.ComponentModel.DataAnnotations;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Paging;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HelpDesk.Api.Features.Tickets;

// Una lectura sin reglas: el endpoint le pregunta directo a su consulta. Un Handler aquí solo sería una capa más.
public static class ListTicketActivity
{
    public static void Map(IEndpointRouteBuilder tickets) =>
        tickets.MapGet("/activity", HandleAsync)
            .WithName("ListActivity")
            .WithSummary("Tickets abiertos de todo el equipo, por páginas, con sus comentarios más recientes. Solo supervisión.")
            .RequireAuthorization(HelpDeskPolicies.Supervision)
            .ProducesValidationProblem();

    private static async Task<Ok<CursorPage<TicketActivityResponse>>> HandleAsync(
        [AsParameters] PageRequest page,
        TicketActivityQuery query,
        CancellationToken cancellationToken,
        [Range(1, 10)] int latestComments = 3) =>
        TypedResults.Ok(await query.ListOpenAsync(page, latestComments, cancellationToken));
}
