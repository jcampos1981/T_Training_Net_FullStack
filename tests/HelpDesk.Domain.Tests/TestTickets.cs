namespace HelpDesk.Domain.Tests;

// Test Data Builder simplificado: valores por defecto razonables, y cada prueba
// cambia solo lo que le importa. Así se nota a simple vista qué dato decide el resultado.
internal static class TestTickets
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public static Ticket Create(
        string title = "Ticket de prueba",
        TicketPriority priority = TicketPriority.Medium,
        TicketStatus status = TicketStatus.Open,
        DateTimeOffset? createdAt = null) =>
        new()
        {
            Id = new TicketId(1),
            Title = title,
            Priority = priority,
            Status = status,
            CreatedAt = createdAt ?? Now
        };
}
