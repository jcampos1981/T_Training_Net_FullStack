using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using HelpDesk.Api.Background;
using HelpDesk.Api.Data;
using HelpDesk.Messaging;
using HelpDesk.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace HelpDesk.Api.Outbox;

// Publica lo pendiente del outbox. Si se cae entre publicar y marcar como publicado, lo vuelve a publicar: la entrega es "al menos una vez",
// y por eso quien consume tiene que ser idempotente. Con RabbitMQ caído no se pierde nada: los eventos esperan en la tabla.
public sealed class OutboxPublisher(
    IServiceScopeFactory scopes,
    RabbitMqPublisherChannel channels,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxPublisher> logger) : PeriodicBackgroundService(scopes, clock, logger)
{
    private static readonly Counter<long> Published = HelpDeskTelemetry.Meter.CreateCounter<long>("helpdesk.outbox.published", description: "Eventos publicados en RabbitMQ");

    private readonly TimeProvider clock = clock;

    public override TimeSpan Interval => options.Value.PublishInterval;

    protected override async Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        HelpDeskDbContext db = services.GetRequiredService<HelpDeskDbContext>();
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
        int published;

        do
        {
            published = await strategy.ExecuteAsync(() => PublishBatchAsync(db, cancellationToken));
        }
        while (published == options.Value.BatchSize);
    }

    private async Task<int> PublishBatchAsync(HelpDeskDbContext db, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // UPDLOCK + READPAST: con varias instancias de la API, cada una toma filas distintas en vez de esperarse o repetir el trabajo.
        List<OutboxMessage> pending = await db.OutboxMessages
            .FromSql($"SELECT TOP({options.Value.BatchSize}) * FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK) WHERE PublishedAt IS NULL ORDER BY Id")
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return 0;
        }

        IChannel channel = await channels.GetAsync(cancellationToken);

        foreach (OutboxMessage message in pending)
        {
            await PublishAsync(channel, message, cancellationToken);
        }

        long[] ids = [.. pending.Select(message => message.Id)];
        DateTimeOffset now = clock.GetUtcNow();
        await db.OutboxMessages
            .Where(message => ids.Contains(message.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.PublishedAt, now), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        Published.Add(pending.Count);

        return pending.Count;
    }

    private static async Task PublishAsync(IChannel channel, OutboxMessage message, CancellationToken cancellationToken)
    {
        // Continúa la traza de la petición que creó el evento.
        using Activity? span = HelpDeskTelemetry.Source.StartActivity($"{message.Type} publish", ActivityKind.Producer, message.TraceParent);
        span?.SetTag("messaging.system", "rabbitmq");
        span?.SetTag("messaging.destination.name", TicketEventsTopology.EventsExchange);
        span?.SetTag("messaging.message.id", message.MessageId);

        var properties = new BasicProperties
        {
            MessageId = message.MessageId.ToString(),
            Type = message.Type,
            ContentType = "application/json",
            Persistent = true, // sobrevive a un reinicio de RabbitMQ, si la cola también es durable
            Headers = new Dictionary<string, object?> { [TicketEventsTopology.TraceParentHeader] = span?.Id ?? message.TraceParent }
        };

        // mandatory: sin esto, si todavía no hay ninguna cola enlazada, RabbitMQ descarta el mensaje y responde que todo salió bien.
        // Así lanza una excepción, y el evento sigue pendiente hasta que alguien lo pueda recibir.
        // Con las confirmaciones activas, esto termina cuando RabbitMQ lo tiene guardado.
        await channel.BasicPublishAsync(
            TicketEventsTopology.EventsExchange,
            message.Type,
            mandatory: true,
            properties,
            Encoding.UTF8.GetBytes(message.Payload),
            cancellationToken);
    }
}
