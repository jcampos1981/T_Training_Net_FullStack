using System.Collections.Concurrent;
using System.Text.Json;
using HelpDesk.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace HelpDesk.Api.IntegrationTests.Support;

public sealed record ReceivedTicketEvent(string MessageId, string Type, string? TraceParent, TicketEventMessage Message);

// Hace de consumidor: una cola propia que recibe todos los eventos, como lo haría el worker. Las pruebas corren en paralelo, así que cada una
// busca los de su propio ticket.
public sealed class TicketEventsListener(IConnection connection, IChannel channel) : IAsyncDisposable
{
    private const string Queue = "pruebas";

    private readonly ConcurrentQueue<ReceivedTicketEvent> received = new();

    public static async Task<TicketEventsListener> StartAsync(string connectionString, CancellationToken cancellationToken)
    {
        IConnection connection = await new ConnectionFactory { Uri = new Uri(connectionString) }.CreateConnectionAsync(cancellationToken);
        IChannel channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await TicketEventsTopology.DeclareEventsExchangeAsync(channel, cancellationToken);
        await channel.QueueDeclareAsync(Queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, TicketEventsTopology.EventsExchange, "ticket.*", cancellationToken: cancellationToken);

        var listener = new TicketEventsListener(connection, channel);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += listener.OnReceivedAsync;
        await channel.BasicConsumeAsync(Queue, autoAck: true, consumer, cancellationToken);

        return listener;
    }

    public Task UnbindAsync() => channel.QueueUnbindAsync(Queue, TicketEventsTopology.EventsExchange, "ticket.*", cancellationToken: Test.Cancel);

    public Task BindAsync() => channel.QueueBindAsync(Queue, TicketEventsTopology.EventsExchange, "ticket.*", cancellationToken: Test.Cancel);

    public IReadOnlyList<ReceivedTicketEvent> Of(int ticketId) => [.. received.Where(item => item.Message.TicketId == ticketId)];

    public async Task<ReceivedTicketEvent> WaitForAsync(int ticketId, string type)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            if (Of(ticketId).FirstOrDefault(item => item.Type == type) is { } found)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Test.Cancel);
        }

        throw new TimeoutException($"No llegó {type} del ticket {ticketId}.");
    }

    public async ValueTask DisposeAsync()
    {
        await channel.DisposeAsync();
        await connection.DisposeAsync();
    }

    private Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        TicketEventMessage message = JsonSerializer.Deserialize<TicketEventMessage>(delivery.Body.Span, JsonSerializerOptions.Web)!;
        string? traceParent = TicketEventsTopology.ReadTextHeader(delivery.BasicProperties, TicketEventsTopology.TraceParentHeader);

        received.Enqueue(new ReceivedTicketEvent(delivery.BasicProperties.MessageId!, delivery.BasicProperties.Type!, traceParent, message));

        return Task.CompletedTask;
    }
}
