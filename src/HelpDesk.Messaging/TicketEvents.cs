using System.Text;
using RabbitMQ.Client;

namespace HelpDesk.Messaging;

// El tipo de cada evento, que además es su routing key en RabbitMQ.
public static class TicketEventType
{
    public const string Created = "ticket.created";
    public const string Closed = "ticket.closed";
}

// El contrato: un evento ya publicado no se puede cambiar, porque puede haber consumidores de versiones distintas leyéndolo.
// Se agregan campos opcionales, y un cambio incompatible es un evento nuevo.
public sealed record TicketEventMessage(int TicketId, string Code, string Title, string? CustomerEmail, DateTimeOffset OccurredAt);

public static class TicketEventsTopology
{
    public const string EventsExchange = "helpdesk.events";
    public const string EmailsQueue = "helpdesk.ticket-emails";
    public const string EmbeddingsQueue = "helpdesk.ticket-embeddings";

    public const string TraceParentHeader = "traceparent";
    public const string AttemptHeader = "x-attempt";

    // Con la espera en el nombre: RabbitMQ no deja cambiar los argumentos de una cola que ya existe, y el worker no arrancaría al cambiar RetryDelay.
    // Así, cada espera tiene su cola. Lo que quedó en la anterior vuelve solo a la cola principal, y esa cola vacía se puede borrar.
    public static string RetryQueueOf(string queue, TimeSpan retryDelay) => $"{queue}.retry.{(long)retryDelay.TotalMilliseconds}ms";

    public static string DeadQueueOf(string queue) => $"{queue}.dead";

    public static Task DeclareEventsExchangeAsync(IChannel channel, CancellationToken cancellationToken) =>
        channel.ExchangeDeclareAsync(EventsExchange, ExchangeType.Topic, durable: true, cancellationToken: cancellationToken);

    // Un mensaje que falla no se reintenta enseguida, porque seguiría fallando y bloquearía a los demás. Va a una cola sin consumidores,
    // donde espera; al vencer vuelve sola a la cola principal. Después de varios intentos queda en la cola de muertos, para revisarla a mano.
    public static async Task DeclareConsumerQueuesAsync(IChannel channel, string queue, string bindingKey, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        await DeclareEventsExchangeAsync(channel, cancellationToken);

        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queue, EventsExchange, bindingKey, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            RetryQueueOf(queue, retryDelay),
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (int)retryDelay.TotalMilliseconds,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = queue
            },
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(DeadQueueOf(queue), durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
    }

    // RabbitMQ entrega los headers de texto como bytes.
    public static string? ReadTextHeader(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers?.TryGetValue(name, out object? value) == true && value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;
}
