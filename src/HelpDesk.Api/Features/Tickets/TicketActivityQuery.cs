using Dapper;
using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Features.Tickets;

// Fase 3: una proyección de EF Core resolvió el N+1 (9 consultas → 1), pero a escala su forma no aguanta:
// EF numera con ROW_NUMBER() TODOS los comentarios (2.5 M, 590 MB de memoria) antes de quedarse con 3 por ticket.
// Fase 4: SQL a mano. Primero la página de tickets; después, SOLO para esos tickets, sus últimos N comentarios
// con OUTER APPLY + TOP, que usa el índice (TicketId, CreatedAt) para ir directo a ellos.
public sealed class TicketActivityQuery(HelpDeskDbContext db)
{
    private const string ActivitySql = """
        -- Actividad de tickets abiertos (página por keyset)
        SELECT t.Id, t.Title, t.Status, cc.CommentCount,
               c.Id AS CommentId, c.Author, c.Body, c.CreatedAt
        FROM (
            SELECT TOP (@Take) tk.Id, tk.Title, tk.Status
            FROM dbo.Tickets AS tk
            WHERE tk.Status IN (N'Open', N'InProgress') AND tk.Id > @After
            ORDER BY tk.Id
        ) AS t
        CROSS APPLY (
            SELECT COUNT(*) AS CommentCount
            FROM dbo.TicketComments AS x
            WHERE x.TicketId = t.Id
        ) AS cc
        OUTER APPLY (
            SELECT TOP (@LatestComments) y.Id, y.Author, y.Body, y.CreatedAt
            FROM dbo.TicketComments AS y
            WHERE y.TicketId = t.Id
            ORDER BY y.CreatedAt DESC
        ) AS c
        ORDER BY t.Id, c.CreatedAt DESC;
        """;

    public async Task<CursorPage<TicketActivityResponse>> ListOpenAsync(
        PageRequest page,
        int latestComments,
        CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(
            ActivitySql,
            new { Take = page.Take(), page.After, LatestComments = latestComments },
            cancellationToken: cancellationToken);

        IEnumerable<ActivityRow> rows = await db.Database.GetDbConnection().QueryAsync<ActivityRow>(command);

        // Una fila por comentario (o una sola, sin comentario, si el ticket no tiene): se agrupan por ticket.
        List<TicketActivityResponse> tickets = [.. rows.GroupBy(row => row.Id).Select(ToResponse)];

        return CursorPage.Create(tickets, page, ticket => ticket, ticket => ticket.Id);
    }

    private static TicketActivityResponse ToResponse(IGrouping<int, ActivityRow> rows)
    {
        ActivityRow ticket = rows.First();

        List<CommentResponse> latest =
        [
            .. rows.Where(row => row.CommentId is not null)
                   .Select(row => new CommentResponse(row.CommentId!.Value, row.Author!, row.Body!, row.CreatedAt!.Value))
        ];

        return new TicketActivityResponse(
            ticket.Id,
            ticket.Title,
            Enum.Parse<TicketStatus>(ticket.Status),
            ticket.CommentCount,
            latest);
    }

    // Forma "plana" de cada fila del SELECT; Dapper la llena por nombre de columna.
    private sealed class ActivityRow
    {
        public int Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int CommentCount { get; init; }
        public int? CommentId { get; init; }
        public string? Author { get; init; }
        public string? Body { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
    }
}
