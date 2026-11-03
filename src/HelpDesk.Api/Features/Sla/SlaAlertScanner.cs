using System.Data.Common;
using System.Text.Json;
using Dapper;
using HelpDesk.Api.Data;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Features.Sla;

// Busca los tickets abiertos que llegaron a un nuevo nivel de SLA y crea sus alertas. Con más de un millón de tickets, la
// búsqueda tiene que ocurrir en la base: el dominio (ISlaPolicy) dice dónde están las fronteras, y el SQL solo las aplica.
public sealed class SlaAlertScanner(
    HelpDeskDbContext db,
    ISlaPolicy policy,
    IOptions<SlaMonitorOptions> options,
    TimeProvider clock)
{
    // Una sola sentencia reclama las alertas y dice cuáles consiguió:
    // - NOT EXISTS: las que ya existen no se vuelven a crear. Un ticket que ya venció no pasa por "en riesgo".
    // - IGNORE_DUP_KEY (en el índice único): si otra instancia de la API creó la misma alerta en ese instante, esa fila se
    //   descarta sin error, y OUTPUT solo devuelve las que de verdad se insertaron. Por eso cada alerta se avisa una vez.
    // - El literal de Status es lo que permite usar el índice filtrado de tickets abiertos, no el millón completo.
    // - El hint: por el TOP, el optimizador supone que encontrará pronto las filas y elige Nested Loops. Casi siempre no hay
    //   nada nuevo, así que recorría todo: 154 mil lecturas por vuelta, contra 1.159 sin esa suposición (caso 19 del diario).
    private const string ClaimBatchSql = """
        -- SLA: reclamar alertas nuevas
        DECLARE @claimed TABLE (TicketId int NOT NULL, Level nvarchar(20) NOT NULL);

        INSERT INTO dbo.SlaAlerts (TicketId, Level, RaisedAt)
        OUTPUT inserted.TicketId, inserted.Level INTO @claimed
        SELECT TOP (@BatchSize) due.Id, due.Level, @Now
        FROM (
            SELECT t.Id, CASE WHEN t.CreatedAt <= c.BreachAt THEN N'Breached' ELSE N'AtRisk' END AS Level
            FROM dbo.Tickets AS t
            JOIN OPENJSON(@Cutoffs) WITH (Priority nvarchar(20), AtRiskAt datetimeoffset, BreachAt datetimeoffset) AS c
                ON c.Priority = t.Priority
            WHERE t.Status IN (N'Open', N'InProgress')
              AND t.CreatedAt <= c.AtRiskAt
        ) AS due
        WHERE NOT EXISTS (SELECT 1 FROM dbo.SlaAlerts AS a WHERE a.TicketId = due.Id AND a.Level = due.Level)
        OPTION (USE HINT('DISABLE_OPTIMIZER_ROWGOAL'));

        SELECT c.TicketId, c.Level, t.Title, t.AssignedTo
        FROM @claimed AS c
        JOIN dbo.Tickets AS t ON t.Id = c.TicketId;
        """;

    public async Task<IReadOnlyList<RaisedSlaAlert>> ScanAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        int batchSize = options.Value.BatchSize;
        object parameters = new { Cutoffs = CutoffsAt(now), BatchSize = batchSize, Now = now };
        DbConnection connection = db.Database.GetDbConnection();

        List<RaisedSlaAlert> raised = [];
        int claimed;

        // Si hay mucho acumulado (el primer arranque, o la API estuvo caída), se avanza por lotes hasta terminar.
        do
        {
            var command = new CommandDefinition(ClaimBatchSql, parameters, cancellationToken: cancellationToken);
            List<RaisedSlaAlert> batch = [.. await connection.QueryAsync<RaisedSlaAlert>(command)];

            raised.AddRange(batch);
            claimed = batch.Count;
        }
        while (claimed == batchSize);

        return raised;
    }

    // JSON para que el SQL no conozca las prioridades: una nueva en el dominio no obliga a tocar esta consulta.
    private string CutoffsAt(DateTimeOffset now) =>
        JsonSerializer.Serialize(Enum.GetValues<TicketPriority>().Select(priority => new
        {
            Priority = priority.ToString(),
            AtRiskAt = now - policy.AtRiskAfter(priority),
            BreachAt = now - policy.ResolutionTimeFor(priority)
        }));
}
