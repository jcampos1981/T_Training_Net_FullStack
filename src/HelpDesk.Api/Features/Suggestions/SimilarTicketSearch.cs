using System.Data.Common;
using System.Text.Json;
using Dapper;
using HelpDesk.Ai;
using HelpDesk.Api.Data;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Features.Suggestions;

public sealed record SimilarTicket(int Id, string Title, double Distance);

// Busca por significado con el tipo vector de SQL Server 2025. Sin una base aparte: los vectores viven junto a los tickets, en la misma transacción y
// el mismo respaldo. Recorre todos los vectores (búsqueda exacta), lo que funciona bien con decenas de miles. Para millones haría falta un índice DiskANN.
public sealed class SimilarTicketSearch(HelpDeskDbContext db, IOptions<AiOptions> options)
{
    // Solo los del modelo actual: los vectores de dos modelos no se pueden comparar, aunque midan lo mismo.
    private const string SimilarSql = """
        -- Tickets resueltos parecidos
        SELECT TOP (@Take) t.Id, t.Title, VECTOR_DISTANCE('cosine', e.Embedding, CAST(@Vector AS vector(768))) AS Distance
        FROM dbo.TicketEmbeddings AS e
        JOIN dbo.Tickets AS t ON t.Id = e.TicketId
        WHERE e.Model = @Model AND t.Id <> @CurrentTicket AND t.Status = N'Closed'
        ORDER BY Distance;
        """;

    // Lo último que escribió el equipo: es donde suele estar lo que resolvió el problema. Sin lo de los clientes: además de ruido ("gracias", "sigue igual"),
    // cualquiera podría dejar en su ticket instrucciones para el modelo, que después llegarían al borrador de otro cliente.
    private const string TeamCommentsSql = """
        -- Lo que escribió el equipo en los tickets
        SELECT TicketId, Body
        FROM (
            SELECT c.TicketId, c.Body, ROW_NUMBER() OVER (PARTITION BY c.TicketId ORDER BY c.CreatedAt DESC) AS Position
            FROM dbo.TicketComments AS c
            WHERE c.TicketId IN (SELECT value FROM OPENJSON(@Ids)) AND c.FromCustomer = 0
        ) AS recent
        WHERE recent.Position <= @PerTicket
        ORDER BY TicketId, Position DESC;
        """;

    // El mismo texto que indexa el worker: el título y el primer comentario.
    private const string DescriptionSql = """
        -- Descripción de un ticket
        SELECT TOP (1) Body FROM dbo.TicketComments WHERE TicketId = @Id ORDER BY CreatedAt;
        """;

    public async Task<string?> DescriptionAsync(TicketId ticket, CancellationToken cancellationToken) =>
        await db.Database.GetDbConnection().QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            DescriptionSql,
            new { Id = ticket.Value },
            cancellationToken: cancellationToken));

    public async Task<IReadOnlyList<SimilarTicket>> FindAsync(ReadOnlyMemory<float> vector, TicketId current, CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();
        AiOptions ai = options.Value;

        IEnumerable<SimilarTicket> similar = await connection.QueryAsync<SimilarTicket>(new CommandDefinition(
            SimilarSql,
            new
            {
                Take = ai.SimilarTickets,
                Vector = JsonSerializer.Serialize(vector.ToArray()),
                Model = ai.EmbeddingModel,
                CurrentTicket = current.Value
            },
            cancellationToken: cancellationToken));

        return CaseSelection.Closest([.. similar], ticket => ticket.Distance, ai);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> TeamCommentsAsync(IEnumerable<int> ticketIds, int perTicket, CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        IEnumerable<(int TicketId, string Body)> rows = await connection.QueryAsync<(int TicketId, string Body)>(new CommandDefinition(
            TeamCommentsSql,
            new { Ids = JsonSerializer.Serialize(ticketIds), PerTicket = perTicket },
            cancellationToken: cancellationToken));

        return rows.GroupBy(row => row.TicketId).ToDictionary(group => group.Key, IReadOnlyList<string> (group) => [.. group.Select(row => row.Body)]);
    }
}
