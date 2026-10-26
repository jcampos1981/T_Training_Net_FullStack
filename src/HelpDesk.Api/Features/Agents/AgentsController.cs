using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.Api.Features.Agents;

// La misma API, pero con Controllers: el modelo clásico (heredero de ASP.NET MVC / Web API 2).
// [ApiController] activa validación automática del modelo y respuestas ProblemDetails.
// [FromServices]: cada acción pide solo el servicio que usa, en vez de cargar todos en el constructor.
[ApiController]
[Route("api/agents")]
[Tags("Agents")]
public sealed class AgentsController : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista los agentes con su carga de tickets (abiertos y totales).")]
    public async Task<ActionResult<IReadOnlyList<AgentSummaryResponse>>> List(
        [FromServices] AgentQueries queries,
        CancellationToken cancellationToken)
    {
        return Ok(await queries.SummarizeAsync(cancellationToken));
    }

    [HttpGet("{name}/tickets")]
    [EndpointSummary("Lista por páginas los tickets asignados a un agente (keyset: ?after=&pageSize=).")]
    public async Task<ActionResult<CursorPage<TicketResponse>>> Tickets(
        string name,
        [FromQuery] PageRequest page,
        [FromServices] ITicketRepository repository,
        CancellationToken cancellationToken)
    {
        var filter = new TicketFilter(Status: null, Priority: null, AssignedTo: name);
        IReadOnlyList<Ticket> tickets = await repository.ListAsync(
            filter, TicketsFeature.AfterTicket(page), page.Take(), cancellationToken);

        return Ok(CursorPage.Create(tickets, page, ticket => ticket.ToResponse(), ticket => ticket.Id.Value));
    }

    [HttpPost("{name}/reassign")]
    [EndpointSummary("Reasigna los tickets abiertos de un agente a otro, en una sola transacción.")]
    public async Task<ActionResult<ReassignTicketsResponse>> Reassign(
        string name,
        ReassignTicketsRequest request,
        [FromServices] ReassignTicketsHandler handler,
        CancellationToken cancellationToken)
    {
        string toAgent = request.To.Trim();

        if (string.Equals(name, toAgent, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(request.To), "El agente destino debe ser distinto del de origen.");
            return ValidationProblem(ModelState);
        }

        return Ok(await handler.HandleAsync(name, toAgent, cancellationToken));
    }
}
