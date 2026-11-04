using System.Diagnostics;
using System.Text.Json;
using HelpDesk.Domain;
using HelpDesk.Messaging;

namespace HelpDesk.Api.Outbox;

// Un evento pendiente de publicar. Se guarda en la misma transacción que el ticket: o quedan los dos o ninguno.
public sealed class OutboxMessage
{
    public long Id { get; private set; }

    public required Guid MessageId { get; init; }

    public required string Type { get; init; }

    public required string Payload { get; init; }

    // Para que la traza continúe en quien consuma el evento, aunque se publique segundos después.
    public string? TraceParent { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public static OutboxMessage For(string type, Ticket ticket, DateTimeOffset now) => new()
    {
        MessageId = Guid.NewGuid(),
        Type = type,
        Payload = JsonSerializer.Serialize(
            new TicketEventMessage(ticket.Id.Value, ticket.Id.ToString(), ticket.Title, ticket.RequestedBy, now),
            JsonSerializerOptions.Web),
        TraceParent = Activity.Current?.Id,
        CreatedAt = now
    };
}
