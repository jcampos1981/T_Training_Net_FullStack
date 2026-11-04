using HelpDesk.Api.Caching;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Mediation;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace HelpDesk.Api.Features.Agents;

// La misma API, pero con Controllers: el modelo clásico (heredero de ASP.NET MVC / Web API 2). Los casos de uso no cambian:
// un Controller, igual que un endpoint de Minimal APIs, solo traduce entre HTTP y el caso de uso.
// [ApiController] activa validación automática del modelo y respuestas ProblemDetails.
// [FromServices]: cada acción pide solo el servicio que usa, en vez de cargar todos en el constructor.
// [Authorize] en la clase aplica a todas las acciones; en una acción, se SUMA (hay que cumplir los dos).
[ApiController]
[Route("api/agents")]
[Tags("Agents")]
[Authorize(Policy = HelpDeskPolicies.Staff)]
// Los errores también son contrato: declarados aquí, los clientes generados los reciben tipados. Ojo: con cualquier
// [ProducesResponseType], ASP.NET Core deja de inferir el 200 desde ActionResult<T>, así que cada acción lo declara.
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class AgentsController : ControllerBase
{
    private const string ProblemContentType = "application/problem+json";

    // Una lectura sin reglas: le pregunta directo a su consulta.
    [HttpGet]
    [Authorize(Policy = HelpDeskPolicies.Supervision)]
    [EndpointSummary("Lista los agentes con su carga de tickets (abiertos y totales). Solo supervisión.")]
    [ProducesResponseType<IReadOnlyList<AgentSummaryResponse>>(StatusCodes.Status200OK)]
    [OutputCache(PolicyName = AgentSummaryCache.Policy)]
    public async Task<ActionResult<IReadOnlyList<AgentSummaryResponse>>> List(
        [FromServices] AgentQueries queries,
        CancellationToken cancellationToken)
    {
        return Ok(await queries.SummarizeAsync(cancellationToken));
    }

    [HttpGet("{name}/tickets")]
    [EndpointSummary("Lista por páginas los tickets de un agente (keyset: ?after=&pageSize=). Un agente solo ve los suyos.")]
    [ProducesResponseType<CursorPage<TicketResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    public async Task<ActionResult<CursorPage<TicketResponse>>> Tickets(
        string name,
        [FromQuery] PageRequest page,
        [FromServices] ISender sender,
        CancellationToken cancellationToken)
    {
        Result<CursorPage<TicketResponse>> result = await sender.SendAsync(new ListAgentTickets.Query(name, page, User), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : Failure(result.Error);
    }

    [HttpPost("{name}/reassign")]
    [Authorize(Policy = HelpDeskPolicies.Supervision)]
    [EndpointSummary("Reasigna los tickets abiertos de un agente a otro, en una sola transacción. Solo supervisión.")]
    [ProducesResponseType<ReassignTicketsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<ActionResult<ReassignTicketsResponse>> Reassign(
        string name,
        ReassignTicketsRequest request,
        [FromServices] ISender sender,
        CancellationToken cancellationToken)
    {
        string toAgent = request.To.Trim();

        if (string.Equals(name, toAgent, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(request.To), "El agente destino debe ser distinto del de origen.");
            return ValidationProblem(ModelState);
        }

        Result<ReassignTicketsResponse> result = await sender.SendAsync(new ReassignTickets.Command(name, toAgent, User), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : Failure(result.Error);
    }

    private ActionResult Failure(Error error) => error.Kind switch
    {
        ErrorKind.Forbidden => Forbid(),
        ErrorKind.NotFound => NotFound(),
        _ => Problem(error.Detail, statusCode: StatusCodes.Status409Conflict, title: error.Title)
    };
}
