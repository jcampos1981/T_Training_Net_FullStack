namespace HelpDesk.Domain;

public sealed record Ticket
{
    public required TicketId Id { get; init; }

    public required string Title
    {
        get;
        init => field = DomainGuard.NotBlank(value, nameof(Title), "El título no puede estar vacío.");
    }

    public required TicketPriority Priority { get; init; }

    public TicketStatus Status { get; init; } = TicketStatus.Open;

    public required DateTimeOffset CreatedAt { get; init; }

    public string? AssignedTo { get; init; }

    public bool IsClosed => Status is TicketStatus.Resolved or TicketStatus.Closed;

    public Ticket Update(string title, TicketPriority priority, TicketStatus status, string? assignedTo)
    {
        if (Status is TicketStatus.Closed)
        {
            throw new DomainRuleException($"El ticket {Id} está cerrado y no se puede modificar.");
        }

        // Inmutable: devuelve una copia con los cambios; el setter de Title vuelve a validar.
        return this with { Title = title, Priority = priority, Status = status, AssignedTo = assignedTo };
    }

    // La regla vive en el ticket aunque el comentario se guarde aparte: solo el ticket sabe si está cerrado.
    public TicketComment CreateComment(string author, string body, DateTimeOffset createdAt)
    {
        if (Status is TicketStatus.Closed)
        {
            throw new DomainRuleException($"El ticket {Id} está cerrado y no admite comentarios.");
        }

        return new TicketComment { TicketId = Id, Author = author, Body = body, CreatedAt = createdAt };
    }
}
