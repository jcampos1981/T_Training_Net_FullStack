using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using HelpDesk.Messaging;
using HelpDesk.ServiceDefaults;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace HelpDesk.Worker;

// Patrón Template Method: aquí está lo que necesita todo consumidor (conexión, trazas, reintentos), y cada uno solo dice qué cola lee y qué hace con
// un evento. Confirma cada mensaje a mano y solo al terminar: si el proceso se cae antes, RabbitMQ lo entrega de nuevo.
public abstract class TicketEventConsumer(
    IConfiguration configuration,
    IOptions<TicketEventsOptions> options,
    ILogger logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private static readonly Counter<long> Failed = HelpDeskTelemetry.Meter.CreateCounter<long>("helpdesk.events.failed", description: "Eventos que fallaron al procesarse");

    private static readonly Counter<long> Dead = HelpDeskTelemetry.Meter.CreateCounter<long>("helpdesk.events.dead", description: "Eventos que se dieron por perdidos después de varios intentos");

    private IConnection? connection;
    private IChannel? consumeChannel;
    private IChannel? publishChannel;

    public bool IsConnected => consumeChannel is { IsOpen: true };

    protected abstract string Queue { get; }

    // Qué eventos le interesan, con la sintaxis de un topic de RabbitMQ.
    protected abstract string BindingKey { get; }

    protected abstract Task HandleAsync(Guid messageId, string eventType, TicketEventMessage message, CancellationToken cancellationToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string connectionString = configuration.GetConnectionString("RabbitMq")
            ?? throw new InvalidOperationException("Falta ConnectionStrings:RabbitMq.");

        // Solo el primer intento hay que hacerlo a mano: una vez conectado, el cliente de RabbitMQ se reconecta y recupera el consumidor solo.
        var factory = new ConnectionFactory { Uri = new Uri(connectionString), ClientProvidedName = $"helpdesk-worker-{Queue}" };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(factory, stoppingToken);
                return;
            }
            catch (BrokerUnreachableException exception)
            {
                logger.LogWarning(exception, "RabbitMQ no responde; se vuelve a intentar en {Delay}.", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.CloseAsync(cancellationToken);
        }
        catch (AlreadyClosedException)
        {
            // Al detenerse, el host puede llamar a esto más de una vez, incluso a la vez. Preguntar antes por IsOpen no alcanza: es una carrera.
        }
    }

    public override void Dispose()
    {
        connection?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ConnectAsync(ConnectionFactory factory, CancellationToken cancellationToken)
    {
        connection = await factory.CreateConnectionAsync(cancellationToken);
        consumeChannel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        publishChannel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        await TicketEventsTopology.DeclareConsumerQueuesAsync(consumeChannel, Queue, BindingKey, options.Value.RetryDelay, cancellationToken);
        await consumeChannel.BasicQosAsync(0, (ushort)options.Value.Prefetch, global: false, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(consumeChannel);
        consumer.ReceivedAsync += OnReceivedAsync;
        await consumeChannel.BasicConsumeAsync(Queue, autoAck: false, consumer, cancellationToken);

        logger.LogInformation("Escuchando {Queue}.", Queue);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        string eventType = delivery.BasicProperties.Type ?? delivery.RoutingKey;

        // Continúa la traza de la petición que creó el evento, aunque haya pasado por una cola.
        string? parent = TicketEventsTopology.ReadTextHeader(delivery.BasicProperties, TicketEventsTopology.TraceParentHeader);
        using Activity? span = HelpDeskTelemetry.Source.StartActivity($"{eventType} process", ActivityKind.Consumer, parent);
        span?.SetTag("messaging.system", "rabbitmq");
        span?.SetTag("messaging.destination.name", Queue);
        span?.SetTag("messaging.message.id", delivery.BasicProperties.MessageId);

        try
        {
            TicketEventMessage message = JsonSerializer.Deserialize<TicketEventMessage>(delivery.Body.Span, JsonSerializerOptions.Web)
                ?? throw new JsonException("El evento está vacío.");

            await HandleAsync(Guid.Parse(delivery.BasicProperties.MessageId!), eventType, message, delivery.CancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            span?.SetStatus(ActivityStatusCode.Error);
            Failed.Add(1);
            await SendToRetryOrDeadAsync(delivery, exception);
        }

        await consumeChannel!.BasicAckAsync(delivery.DeliveryTag, multiple: false, delivery.CancellationToken);
    }

    // Se publica una copia y después se confirma el original: si se cae entre medio, llega dos veces, y por eso los consumidores son idempotentes.
    private async Task SendToRetryOrDeadAsync(BasicDeliverEventArgs delivery, Exception exception)
    {
        int attempt = AttemptOf(delivery.BasicProperties) + 1;
        bool givingUp = attempt >= options.Value.MaxAttempts;
        string queue = givingUp ? TicketEventsTopology.DeadQueueOf(Queue) : TicketEventsTopology.RetryQueueOf(Queue, options.Value.RetryDelay);

        if (givingUp)
        {
            Dead.Add(1);
            logger.LogError(exception, "Evento {MessageId} sin procesar después de {Attempt} intentos: queda en {Queue}.", delivery.BasicProperties.MessageId, attempt, queue);
        }
        else
        {
            logger.LogWarning(exception, "Evento {MessageId} falló en el intento {Attempt}: se reintenta.", delivery.BasicProperties.MessageId, attempt);
        }

        var properties = new BasicProperties(delivery.BasicProperties)
        {
            Headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
            {
                [TicketEventsTopology.AttemptHeader] = attempt
            }
        };

        await publishChannel!.BasicPublishAsync(exchange: string.Empty, routingKey: queue, mandatory: true, properties, delivery.Body, delivery.CancellationToken);
    }

    private static int AttemptOf(IReadOnlyBasicProperties properties) =>
        properties.Headers?.TryGetValue(TicketEventsTopology.AttemptHeader, out object? value) == true && value is int attempt ? attempt : 0;
}

// Avisa al cliente por correo.
public sealed class TicketEmailsConsumer(
    IConfiguration configuration,
    TicketEmailHandler handler,
    IOptions<TicketEventsOptions> options,
    ILogger<TicketEmailsConsumer> logger) : TicketEventConsumer(configuration, options, logger)
{
    protected override string Queue => TicketEventsTopology.EmailsQueue;

    protected override string BindingKey => "ticket.*";

    protected override Task HandleAsync(Guid messageId, string eventType, TicketEventMessage message, CancellationToken cancellationToken) =>
        handler.HandleAsync(messageId, eventType, message, cancellationToken);
}

// Un ticket cerrado pasa a ser conocimiento: se indexa para que las sugerencias lo encuentren.
public sealed class TicketEmbeddingsConsumer(
    IConfiguration configuration,
    TicketEmbeddingHandler handler,
    IOptions<TicketEventsOptions> options,
    ILogger<TicketEmbeddingsConsumer> logger) : TicketEventConsumer(configuration, options, logger)
{
    protected override string Queue => TicketEventsTopology.EmbeddingsQueue;

    protected override string BindingKey => TicketEventType.Closed;

    protected override Task HandleAsync(Guid messageId, string eventType, TicketEventMessage message, CancellationToken cancellationToken) =>
        handler.HandleAsync(message.TicketId, cancellationToken);
}
