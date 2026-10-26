using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Comments;

// Solo escritura: las lecturas van por CommentQueries (proyecciones directas a DTO).
public interface ICommentRepository
{
    // Devuelve el comentario ya guardado, con el Id que le asignó la base.
    Task<TicketComment> AddAsync(TicketComment comment, CancellationToken cancellationToken);

    // Cambio masivo: el mismo comentario en cada ticket abierto del agente, con una sola sentencia y sin
    // cargar los tickets. Devuelve cuántos comentarios se crearon.
    Task<int> AddToOpenTicketsAsync(string agent, string author, string body, DateTimeOffset createdAt, CancellationToken cancellationToken);
}
