using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Comments;

// Solo escritura: las lecturas van por CommentQueries (proyecciones directas a DTO).
public interface ICommentRepository
{
    // Devuelve el comentario ya guardado, con el Id que le asignó la base.
    Task<TicketComment> AddAsync(TicketComment comment, CancellationToken cancellationToken);
}
