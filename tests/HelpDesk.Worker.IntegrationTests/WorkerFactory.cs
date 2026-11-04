using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Dapper;
using HelpDesk.Ai;
using HelpDesk.Api.Data;
using HelpDesk.Messaging;
using HelpDesk.Worker;
using HelpDesk.Worker.IntegrationTests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(WorkerFactory))]

namespace HelpDesk.Worker.IntegrationTests;

// Un correo falso: registra lo que se envió, y puede fallar a pedido.
public sealed class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentDictionary<string, int> failuresLeft = new();

    public ConcurrentQueue<TicketEmail> Sent { get; } = new();

    public void FailFor(string ticketCode, int times) => failuresLeft[ticketCode] = times;

    public Task SendAsync(TicketEmail email, CancellationToken cancellationToken)
    {
        if (failuresLeft.TryGetValue(email.TicketCode, out int left) && left > 0)
        {
            failuresLeft[email.TicketCode] = left - 1;
            throw new InvalidOperationException("El servidor de correo no responde.");
        }

        Sent.Enqueue(email);

        return Task.CompletedTask;
    }
}

// Un modelo falso: siempre el mismo vector, y registra qué se le pidió.
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ConcurrentQueue<string> texts = new();
    private readonly ConcurrentBag<string> failing = [];

    public void FailFor(string text) => failing.Add(text);

    public IEnumerable<string> TextsContaining(string text) => texts.Where(item => item.Contains(text, StringComparison.Ordinal));

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        string[] inputs = [.. values];

        if (inputs.Any(input => failing.Any(text => input.Contains(text, StringComparison.Ordinal))))
        {
            throw new HttpRequestException("El modelo no responde.");
        }

        foreach (string input in inputs)
        {
            texts.Enqueue(input);
        }

        float[] vector = new float[AiOptions.EmbeddingDimensions];
        vector[0] = 1;

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(_ => new Embedding<float>(vector))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class WorkerFactory : WebApplicationFactory<TicketEmailsConsumer>, IAsyncLifetime
{
    private const string MaxAttempts = "3";

    private readonly MsSqlContainer sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest")
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
        .Build();

    private readonly RabbitMqContainer rabbitMq = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    private IConnection? connection;
    private IChannel? channel;

    public string ConnectionString { get; private set; } = string.Empty;

    public FakeEmailSender Emails { get; } = new();

    public FakeEmbeddingGenerator Embeddings { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(sqlServer.StartAsync(Cancel), rabbitMq.StartAsync(Cancel));

        ConnectionString = new SqlConnectionStringBuilder(sqlServer.GetConnectionString()) { InitialCatalog = "HelpDesk" }.ConnectionString;
        await MigrateWhenReadyAsync();

        connection = await new ConnectionFactory { Uri = new Uri(rabbitMq.GetConnectionString()) }.CreateConnectionAsync(Cancel);
        channel = await connection.CreateChannelAsync(cancellationToken: Cancel);

        _ = Services; // arranca el worker

        await WaitUntilConnectedAsync();
    }

    public async Task PublishAsync(Guid messageId, string type, TicketEventMessage message) =>
        await PublishRawAsync(messageId, type, JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web));

    public async Task PublishRawAsync(Guid messageId, string type, byte[] body)
    {
        var properties = new BasicProperties { MessageId = messageId.ToString(), Type = type, Persistent = true };

        await channel!.BasicPublishAsync(TicketEventsTopology.EventsExchange, type, mandatory: false, properties, body, Cancel);
    }

    // Lo que hace otra versión del worker al arrancar, con otra espera entre reintentos. En su propio canal: si RabbitMQ lo rechaza, lo cierra.
    public async Task DeclareQueuesWithDelayAsync(TimeSpan retryDelay)
    {
        await using IChannel other = await connection!.CreateChannelAsync(cancellationToken: Cancel);

        await TicketEventsTopology.DeclareConsumerQueuesAsync(other, TicketEventsTopology.EmailsQueue, "ticket.*", retryDelay, Cancel);
    }

    public async Task<int> InsertTicketAsync(string title, string description)
    {
        await using var sql = new SqlConnection(ConnectionString);

        int id = await sql.ExecuteScalarAsync<int>("""
            DECLARE @Id int = NEXT VALUE FOR dbo.TicketIds;
            INSERT INTO dbo.Tickets (Id, Title, Priority, Status, CreatedAt) VALUES (@Id, @Title, N'Low', N'Closed', SYSDATETIMEOFFSET());
            SELECT @Id;
            """, new { Title = title });

        await sql.ExecuteAsync(
            "INSERT INTO dbo.TicketComments (TicketId, Author, Body, CreatedAt) VALUES (@Id, N'Prueba', @Body, SYSDATETIMEOFFSET())",
            new { Id = id, Body = description });

        return id;
    }

    public async Task<int> CountEmbeddingsAsync(int ticketId)
    {
        await using var sql = new SqlConnection(ConnectionString);

        return await sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.TicketEmbeddings WHERE TicketId = @ticketId", new { ticketId });
    }

    public async Task<bool> WaitUntilIndexedAsync(int ticketId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (await CountEmbeddingsAsync(ticketId) > 0)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancel);
        }

        return false;
    }

    public async Task<bool> WaitUntilEmbeddedTimesAsync(string text, int times)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (Embeddings.TextsContaining(text).Count() >= times)
            {
                // Lo que se cuenta es la llamada al modelo: el guardado viene justo después.
                await Task.Delay(TimeSpan.FromMilliseconds(300), Cancel);
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancel);
        }

        return false;
    }

    // Lo que quedó en la cola de muertos, si está ahí ese mensaje. Lo saca de la cola.
    public async Task<bool> IsDeadAsync(Guid messageId, string queue = TicketEventsTopology.EmailsQueue, int attempts = 100)
    {
        string deadQueue = TicketEventsTopology.DeadQueueOf(queue);
        IChannel open = channel ?? throw new InvalidOperationException("Sin canal.");

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            BasicGetResult? dead = await open.BasicGetAsync(deadQueue, autoAck: false, Cancel);

            if (dead?.BasicProperties.MessageId == messageId.ToString())
            {
                await open.BasicAckAsync(dead.DeliveryTag, multiple: false, Cancel);
                return true;
            }

            if (dead is not null)
            {
                await open.BasicNackAsync(dead.DeliveryTag, multiple: false, requeue: true, Cancel);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancel);
        }

        return false;
    }

    public async Task<int> CountAsync(string table, Guid messageId)
    {
        await using var sql = new SqlConnection(ConnectionString);

        return await sql.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE MessageId = @messageId", new { messageId });
    }

    public async Task<string?> SubjectOfAsync(Guid messageId)
    {
        await using var sql = new SqlConnection(ConnectionString);

        return await sql.ExecuteScalarAsync<string?>("SELECT Subject FROM worker.SentEmails WHERE MessageId = @messageId", new { messageId });
    }

    public async Task<bool> WaitUntilProcessedAsync(Guid messageId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (await CountAsync("worker.ProcessedMessages", messageId) > 0)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancel);
        }

        return false;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:HelpDesk", ConnectionString);
        builder.UseSetting("ConnectionStrings:RabbitMq", rabbitMq.GetConnectionString());
        builder.UseSetting("TicketEvents:RetryDelay", "00:00:00.300");
        builder.UseSetting("TicketEvents:MaxAttempts", MaxAttempts);
        builder.UseSetting("Ai:Enabled", "true");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailSender>(Emails);
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Embeddings);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (channel is not null)
        {
            await channel.DisposeAsync();
        }

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }

        await sqlServer.DisposeAsync();
        await rabbitMq.DisposeAsync();
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private async Task MigrateWhenReadyAsync()
    {
        await using var db = new HelpDeskDbContext(new DbContextOptionsBuilder<HelpDeskDbContext>().UseSqlServer(ConnectionString).Options);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(Cancel);
                return;
            }
            catch (SqlException) when (attempt < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), Cancel);
            }
        }
    }

    private async Task WaitUntilConnectedAsync()
    {
        TicketEventConsumer[] consumers = [.. Services.GetServices<TicketEventConsumer>()];
        Assert.Equal(2, consumers.Length);

        for (int attempt = 0; attempt < 100 && !consumers.All(consumer => consumer.IsConnected); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancel);
        }

        Assert.True(consumers.All(consumer => consumer.IsConnected), "El worker no se conectó a RabbitMQ.");

        // IsConnected se vuelve verdadero antes de que termine de declarar sus colas.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await channel!.QueueDeclarePassiveAsync(TicketEventsTopology.DeadQueueOf(TicketEventsTopology.EmailsQueue), Cancel);
                await channel.QueueDeclarePassiveAsync(TicketEventsTopology.DeadQueueOf(TicketEventsTopology.EmbeddingsQueue), Cancel);
                return;
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                channel = await connection!.CreateChannelAsync(cancellationToken: Cancel);
                await Task.Delay(TimeSpan.FromMilliseconds(100), Cancel);
            }
        }
    }
}
