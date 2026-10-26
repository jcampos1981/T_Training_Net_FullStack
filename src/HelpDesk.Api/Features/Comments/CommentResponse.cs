using System.Linq.Expressions;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Comments;

public sealed record CommentResponse(int Id, string Author, string Body, DateTimeOffset CreatedAt)
{
    // Una expresión, no un método: EF Core puede "leerla" y traducirla a las columnas del SELECT.
    // La misma definición sirve para consultas (SQL) y para mapear en memoria (From).
    public static readonly Expression<Func<TicketComment, CommentResponse>> Projection =
        comment => new CommentResponse(comment.Id, comment.Author, comment.Body, comment.CreatedAt);

    private static readonly Func<TicketComment, CommentResponse> Map = Projection.Compile();

    public static CommentResponse From(TicketComment comment) => Map(comment);
}
