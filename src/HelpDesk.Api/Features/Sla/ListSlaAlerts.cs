using System.Security.Claims;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Mediation;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Sla;

public static class ListSlaAlerts
{
    public sealed record Query(SlaState? Level, PageRequest Page, ClaimsPrincipal User) : IRequest<Result<CursorPage<SlaAlertResponse>>>;

    public sealed class Handler(SlaAlertQueries queries) : IRequestHandler<Query, Result<CursorPage<SlaAlertResponse>>>
    {
        public async Task<Result<CursorPage<SlaAlertResponse>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            // La misma regla que GET /api/tickets: un agente solo ve lo suyo.
            var everything = new TicketFilter(Status: null, Priority: null, AssignedTo: null);
            if (TicketAuthorizationHandler.ScopeListFilter(query.User, everything) is not { } allowed)
            {
                return Error.Forbidden;
            }

            PageRequest page = query.Page;
            IReadOnlyList<SlaAlertResponse> alerts = await queries.ListAsync(
                allowed.AssignedTo, query.Level, page.After > 0 ? page.After : null, page.Take(), cancellationToken);

            return CursorPage.Create(alerts, page, alert => alert, alert => alert.Id);
        }
    }

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/sla/alerts", HandleAsync)
            .WithTags("SLA")
            .WithName(nameof(ListSlaAlerts))
            .WithSummary("Alertas de SLA de tickets abiertos, las más recientes primero (keyset: ?after=&pageSize=). Un agente solo ve las suyas.")
            .RequireAuthorization(HelpDeskPolicies.Staff)
            .Produces<CursorPage<SlaAlertResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    private static async Task<IResult> HandleAsync(
        [AsParameters] PageRequest page,
        ClaimsPrincipal user,
        ISender sender,
        CancellationToken cancellationToken,
        SlaState? level = null) =>
        (await sender.SendAsync(new Query(level, page, user), cancellationToken)).ToHttp(alerts => TypedResults.Ok(alerts));
}
