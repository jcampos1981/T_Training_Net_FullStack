using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.Api.Features.Agents;

// La misma API, pero con Controllers: el modelo clásico (heredero de ASP.NET MVC / Web API 2).
// [ApiController] activa validación automática del modelo y respuestas ProblemDetails.
[ApiController]
[Route("api/agents")]
[Tags("Agents")]
public sealed class AgentsController(ITicketRepository repository) : ControllerBase
{
    private static readonly TicketFilter AllTickets = new(Status: null, Priority: null, AssignedTo: null);

    [HttpGet]
    [EndpointSummary("Lista los agentes con su carga de tickets (abiertos y totales).")]
    public async Task<ActionResult<IReadOnlyList<AgentSummaryResponse>>> List(CancellationToken cancellationToken)
    {
        IReadOnlyList<Ticket> tickets = await repository.ListAsync(AllTickets, cancellationToken);
        return Ok(Summarize(tickets));
    }

    [HttpGet("{name}/tickets")]
    [EndpointSummary("Lista los tickets asignados a un agente.")]
    public async Task<ActionResult<IReadOnlyList<TicketResponse>>> Tickets(string name, CancellationToken cancellationToken)
    {
        var filter = new TicketFilter(Status: null, Priority: null, AssignedTo: name);
        IReadOnlyList<Ticket> tickets = await repository.ListAsync(filter, cancellationToken);

        return tickets.Count == 0
            ? NotFound()
            : Ok(tickets.Select(ticket => ticket.ToResponse()).ToList());
    }

    // [FromServices]: este servicio solo lo usa esta acción, así que se pide aquí y no en el constructor.
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

    private static List<AgentSummaryResponse> Summarize(IEnumerable<Ticket> tickets) =>
        tickets.Where(ticket => ticket.AssignedTo is not null)
               .GroupBy(ticket => ticket.AssignedTo!, StringComparer.OrdinalIgnoreCase)
               .Select(group => new AgentSummaryResponse(
                   group.Key,
                   OpenTickets: group.Count(ticket => !ticket.IsClosed),
                   TotalTickets: group.Count()))
               .OrderByDescending(summary => summary.OpenTickets)
               .ThenBy(summary => summary.Name)
               .ToList();
}
