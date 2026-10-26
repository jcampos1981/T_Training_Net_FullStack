using HelpDesk.Api.Data;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Features.Comments;

public sealed class EfCommentRepository(HelpDeskDbContext db) : ICommentRepository
{
    public async Task<TicketComment> AddAsync(TicketComment comment, CancellationToken cancellationToken)
    {
        db.Comments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);

        // Tras el INSERT, EF escribió en el objeto el Id generado por el IDENTITY.
        return comment;
    }

    // EF Core no tiene INSERT ... SELECT en LINQ: SQL a mano, como en las consultas con Dapper.
    // - ExecuteSql convierte cada {valor} interpolado en un parámetro: no es concatenar texto.
    // - El IN va con literales para que SQL Server use el índice filtrado IX_Tickets_Open_AssignedTo (caso 2):
    //   así lee solo los Id del agente, sin tocar la tabla.
    // - El comentario de la primera línea hace de TagWith: identifica la sentencia en los logs.
    // - La regla de Ticket.CreateComment (un ticket cerrado no admite comentarios) la cumple el mismo WHERE.
    //   El autor y el texto los arma el caso de uso, no un usuario, por eso no pasan por DomainGuard.
    public Task<int> AddToOpenTicketsAsync(
        string agent,
        string author,
        string body,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"""
            -- Comentario de reasignación en cada ticket abierto del agente

            INSERT INTO [TicketComments] ([TicketId], [Author], [Body], [CreatedAt])
            SELECT [t].[Id], {author}, {body}, {createdAt}
            FROM [Tickets] AS [t]
            WHERE [t].[AssignedTo] = {agent} AND [t].[Status] IN (N'Open', N'InProgress')
            """,
            cancellationToken);
}
