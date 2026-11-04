using HelpDesk.Api.Data;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Features.Sla;

public sealed class SlaAlertQueries(HelpDeskDbContext db, ISlaPolicy policy)
{
    // Las alertas de tickets que siguen abiertos, las más recientes primero. Keyset al revés: "Id < before".
    // agent: null = de todo el equipo. Un ticket que se resolvió deja de aparecer, sin borrar su alerta.
    public async Task<IReadOnlyList<SlaAlertResponse>> ListAsync(
        string? agent,
        SlaState? level,
        int? before,
        int take,
        CancellationToken cancellationToken)
    {
        var query = db.SlaAlerts.AsNoTracking().TagWith("Alertas de SLA")
            .Join(db.Tickets, alert => alert.TicketId, ticket => ticket.Id, (alert, ticket) => new { alert, ticket })
            .Where(row => row.ticket.Status == TicketStatus.Open || row.ticket.Status == TicketStatus.InProgress)
            // Un ticket que ya venció tiene dos alertas: la de "en riesgo" ya no dice nada nuevo.
            .Where(row => row.alert.Level == SlaState.Breached
                || !db.SlaAlerts.Any(other => other.TicketId == row.alert.TicketId && other.Level == SlaState.Breached));

        if (AgentName.From(agent) is { } assignedTo)
        {
            query = query.Where(row => row.ticket.AssignedTo == assignedTo);
        }

        if (level is not null)
        {
            query = query.Where(row => row.alert.Level == level);
        }

        if (before is not null)
        {
            query = query.Where(row => row.alert.Id < before);
        }

        var rows = await query
            .OrderByDescending(row => row.alert.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new SlaAlertResponse(
            row.alert.Id,
            row.ticket.Id.Value,
            row.ticket.Id.ToString(),
            row.ticket.Title,
            row.ticket.Priority,
            row.ticket.Status,
            row.ticket.AssignedTo?.Value,
            row.alert.Level,
            policy.DeadlineOf(row.ticket),
            row.alert.RaisedAt))];
    }
}
