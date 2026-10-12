namespace HelpDesk.Domain;

public sealed record Ticket
{
    public required TicketId Id { get; init; }

    public required string Title
    {
        get;
        init => field = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("El título no puede estar vacío.", nameof(Title))
            : value.Trim();
    }

    public required TicketPriority Priority { get; init; }

    public TicketStatus Status { get; init; } = TicketStatus.Open;

    public required DateTimeOffset CreatedAt { get; init; }

    public string? AssignedTo { get; init; }

    public bool IsClosed => Status is TicketStatus.Resolved or TicketStatus.Closed;
}
