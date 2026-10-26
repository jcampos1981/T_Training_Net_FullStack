namespace HelpDesk.Domain;

// Un comentario apunta a su ticket por Id (no por una referencia al objeto Ticket):
// cada uno se guarda y se consulta por separado, sin cargar el otro.
public sealed record TicketComment
{
    // 0 hasta que se guarda: a diferencia de TicketId, aquí sí lo asigna la base (IDENTITY) al insertar.
    public int Id { get; init; }

    public required TicketId TicketId { get; init; }

    public required string Author
    {
        get;
        init => field = DomainGuard.NotBlank(value, nameof(Author), "El autor del comentario es obligatorio.");
    }

    public required string Body
    {
        get;
        init => field = DomainGuard.NotBlank(value, nameof(Body), "El comentario no puede estar vacío.");
    }

    public required DateTimeOffset CreatedAt { get; init; }
}
