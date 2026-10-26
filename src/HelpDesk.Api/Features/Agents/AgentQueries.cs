using HelpDesk.Api.Data;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Features.Agents;

// Antes: el controller pedía TODOS los tickets al repositorio y los agrupaba en memoria (LINQ to Objects):
// con un millón de tickets, un millón de objetos viajando de la base a la API para devolver 9 filas.
// Ahora: el GroupBy se traduce a SQL (GROUP BY + COUNT) y solo viajan los totales.
public sealed class AgentQueries(HelpDeskDbContext db)
{
    public async Task<IReadOnlyList<AgentSummaryResponse>> SummarizeAsync(CancellationToken cancellationToken) =>
        await db.Tickets
            .TagWith("Resumen de carga por agente")
            .Where(ticket => ticket.AssignedTo != null)
            .GroupBy(ticket => ticket.AssignedTo!)
            .Select(group => new
            {
                Agent = group.Key,
                Open = group.Count(ticket => ticket.Status == TicketStatus.Open || ticket.Status == TicketStatus.InProgress),
                Total = group.Count()
            })
            .OrderByDescending(summary => summary.Open)
            .ThenBy(summary => summary.Agent)
            .Select(summary => new AgentSummaryResponse(summary.Agent, summary.Open, summary.Total))
            .ToListAsync(cancellationToken);
}
