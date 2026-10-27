using HelpDesk.Api.Data;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Features.Comments;

// Lado de lectura: proyecciones directas a DTO. EF solo trae las columnas del Select y,
// como no devuelve entidades, no hay nada que rastrear (sin costo de tracking).
public sealed class CommentQueries(HelpDeskDbContext db)
{
    public async Task<IReadOnlyList<CommentResponse>> ListForTicketAsync(TicketId ticketId, CancellationToken cancellationToken) =>
        await db.Comments
            .TagWith("Comentarios de un ticket") // aparece como comentario en el SQL: fácil de encontrar en logs
            .Where(comment => comment.TicketId == ticketId)
            .OrderByDescending(comment => comment.CreatedAt)
            .Select(CommentResponse.Projection)
            .ToListAsync(cancellationToken);

    public Task<CommentResponse?> FindAsync(TicketId ticketId, int commentId, CancellationToken cancellationToken) =>
        db.Comments
            .Where(comment => comment.TicketId == ticketId && comment.Id == commentId)
            .Select(CommentResponse.Projection)
            .FirstOrDefaultAsync(cancellationToken);
}
