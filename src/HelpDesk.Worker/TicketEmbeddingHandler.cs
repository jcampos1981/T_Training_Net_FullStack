using System.Text.Json;
using Dapper;
using HelpDesk.Ai;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;

namespace HelpDesk.Worker;

// Guarda el vector de un ticket cerrado. Repetirlo da el mismo resultado (MERGE por ticket), así que un mensaje repetido no necesita más control.
public sealed class TicketEmbeddingHandler(
    IConfiguration configuration,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    TimeProvider clock,
    ILogger<TicketEmbeddingHandler> logger)
{
    // El primer comentario es la descripción del problema. Lo demás es la solución, y lo que se compara es el problema con una pregunta nueva.
    private const string ReadTicket = """
        SELECT t.Title, (SELECT TOP (1) c.Body FROM dbo.TicketComments AS c WHERE c.TicketId = t.Id ORDER BY c.CreatedAt) AS Description
        FROM dbo.Tickets AS t
        WHERE t.Id = @TicketId;
        """;

    private const string SaveEmbedding = """
        MERGE dbo.TicketEmbeddings WITH (HOLDLOCK) AS target
        USING (SELECT @TicketId AS TicketId) AS source ON target.TicketId = source.TicketId
        WHEN MATCHED THEN UPDATE SET Embedding = CAST(@Vector AS vector(768)), Model = @Model, EmbeddedAt = @Now
        WHEN NOT MATCHED THEN INSERT (TicketId, Embedding, Model, EmbeddedAt) VALUES (@TicketId, CAST(@Vector AS vector(768)), @Model, @Now);
        """;

    private readonly string connectionString = configuration.GetConnectionString("HelpDesk")
        ?? throw new InvalidOperationException("Falta ConnectionStrings:HelpDesk.");

    private readonly AiOptions options = AiSetup.Read(configuration);

    public async Task HandleAsync(int ticketId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);

        (string Title, string? Description)? ticket = await connection.QuerySingleOrDefaultAsync<(string Title, string? Description)?>(
            new CommandDefinition(ReadTicket, new { TicketId = ticketId }, cancellationToken: cancellationToken));

        // Lo borraron después de cerrarlo: no hay nada que indexar, y reintentar no lo traería de vuelta.
        if (ticket is null)
        {
            return;
        }

        ReadOnlyMemory<float> vector = await embeddings.GenerateVectorAsync(
            ReplyPrompt.SearchTextFor(ticket.Value.Title, ticket.Value.Description),
            cancellationToken: cancellationToken);

        if (vector.Length != AiOptions.EmbeddingDimensions)
        {
            throw new InvalidOperationException($"El modelo {options.EmbeddingModel} devuelve {vector.Length} dimensiones, y la columna tiene {AiOptions.EmbeddingDimensions}.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            SaveEmbedding,
            new { TicketId = ticketId, Vector = JsonSerializer.Serialize(vector.ToArray()), Model = options.EmbeddingModel, Now = clock.GetUtcNow() },
            cancellationToken: cancellationToken));

        logger.LogInformation("Ticket {TicketId} indexado.", ticketId);
    }
}
