using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Features.Tickets;

public sealed class TicketActivityQuery(HelpDeskDbContext db)
{
    // UNA consulta: se describe la forma completa del resultado (tickets + conteo + últimos comentarios)
    // y EF Core la traduce a un solo SELECT con subconsultas, en vez de ir a la base por cada ticket (N+1).
    public async Task<IReadOnlyList<TicketActivityResponse>> ListOpenAsync(int latestComments, CancellationToken cancellationToken) =>
        await db.Tickets
            .TagWith("Actividad de tickets abiertos")
            // IsClosed es una propiedad C#: SQL no la conoce, por eso aquí la condición va escrita completa.
            .Where(ticket => ticket.Status == TicketStatus.Open || ticket.Status == TicketStatus.InProgress)
            .OrderBy(ticket => ticket.Id)
            .Select(ticket => new TicketActivityResponse(
                ticket.Id.Value,
                ticket.Title,
                ticket.Status,
                db.Comments.Count(comment => comment.TicketId == ticket.Id),
                db.Comments
                    .Where(comment => comment.TicketId == ticket.Id)
                    .OrderByDescending(comment => comment.CreatedAt)
                    .Take(latestComments)
                    .Select(CommentResponse.Projection)
                    .ToList()))
            .ToListAsync(cancellationToken);
}
