using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Dapper;
using HelpDesk.Api.Data;
using HelpDesk.Messaging;
using HelpDesk.Worker;
using HelpDesk.Worker.IntegrationTests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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

    // Lo que quedó en la cola de muertos, si está ahí ese mensaje. Lo saca de la cola.
    public async Task<bool> IsDeadAsync(Guid messageId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            BasicGetResult? dead = await channel!.BasicGetAsync(TicketEventsTopology.EmailsDeadQueue, autoAck: false, Cancel);

            if (dead?.BasicProperties.MessageId == messageId.ToString())
            {
                await channel.BasicAckAsync(dead.DeliveryTag, multiple: false, Cancel);
                return true;
            }

            if (dead is not null)
            {
                await channel.BasicNackAsync(dead.DeliveryTag, multiple: false, requeue: true, Cancel);
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
        builder.UseSetting("TicketEmails:RetryDelay", "00:00:00.300");
        builder.UseSetting("TicketEmails:MaxAttempts", MaxAttempts);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(Emails));
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
        TicketEmailsConsumer consumer = Services.GetRequiredService<TicketEmailsConsumer>();

        for (int attempt = 0; attempt < 100 && !consumer.IsConnected; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancel);
        }

        Assert.True(consumer.IsConnected, "El worker no se conectó a RabbitMQ.");

        // IsConnected se vuelve verdadero antes de que termine de declarar sus colas.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await channel!.QueueDeclarePassiveAsync(TicketEventsTopology.EmailsDeadQueue, Cancel);
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
