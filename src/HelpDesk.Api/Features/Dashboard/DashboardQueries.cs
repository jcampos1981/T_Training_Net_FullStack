using System.Data.Common;
using Dapper;
using HelpDesk.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Features.Dashboard;

// Dapper: SQL escrito a mano, mapeado directo a records. Ideal para reportes donde ya sabes
// exactamente qué SQL quieres (agregaciones, CASE, funciones de fecha) y EF solo estorbaría.
public sealed class DashboardQueries(HelpDeskDbContext db)
{
    // Tres consultas en UN viaje a la base: Dapper las lee como tres resultados (QueryMultiple).
    // Parámetros con @: Dapper los envía parametrizados, nunca concatenados (sin inyección SQL).
    private const string DashboardSql = """
        -- Dashboard: tickets por estado
        SELECT t.Status, COUNT(*) AS Total
        FROM dbo.Tickets AS t
        GROUP BY t.Status
        ORDER BY Total DESC;

        -- Dashboard: carga abierta por agente (críticos primero) y antigüedad del ticket más viejo
        SELECT
            COALESCE(t.AssignedTo, N'(sin asignar)')                AS Agent,
            COUNT(*)                                                AS OpenTickets,
            SUM(CASE WHEN t.Priority = N'Critical' THEN 1 ELSE 0 END) AS CriticalTickets,
            DATEDIFF(HOUR, MIN(t.CreatedAt), @Now)                  AS OldestOpenHours
        FROM dbo.Tickets AS t
        WHERE t.Status IN (N'Open', N'InProgress')
        GROUP BY t.AssignedTo
        ORDER BY CriticalTickets DESC, OpenTickets DESC;

        -- Dashboard: comentarios por día en la última semana
        SELECT CAST(c.CreatedAt AS date) AS Day, COUNT(*) AS Comments
        FROM dbo.TicketComments AS c
        WHERE c.CreatedAt >= @Since
        GROUP BY CAST(c.CreatedAt AS date)
        ORDER BY Day;
        """;

    public async Task<DashboardResponse> GetAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Se reutiliza la conexión de EF Core: misma cadena de conexión y mismo ciclo de vida (por petición).
        DbConnection connection = db.Database.GetDbConnection();

        var command = new CommandDefinition(
            DashboardSql,
            new { Now = now, Since = now.AddDays(-7) },
            cancellationToken: cancellationToken);

        using SqlMapper.GridReader results = await connection.QueryMultipleAsync(command);

        IReadOnlyList<StatusCount> byStatus = [.. await results.ReadAsync<StatusCount>()];
        IReadOnlyList<AgentWorkloadRow> workload = [.. await results.ReadAsync<AgentWorkloadRow>()];
        IReadOnlyList<DailyCommentCount> activity = [.. await results.ReadAsync<DailyCommentCount>()];

        return new DashboardResponse(now, byStatus, workload, activity);
    }
}
