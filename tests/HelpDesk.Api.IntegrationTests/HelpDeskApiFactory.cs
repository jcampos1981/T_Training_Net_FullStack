using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Accounts.Tokens;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests;
using HelpDesk.Api.IntegrationTests.Support;
using HelpDesk.Api.Outbox;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;

// Un solo SQL Server y una sola API para toda la corrida: levantar el contenedor tarda demasiado para hacerlo por prueba.
// Por eso cada prueba crea sus propios datos, y compara contra ellos, no contra totales de la tabla.
[assembly: AssemblyFixture(typeof(HelpDeskApiFactory))]

namespace HelpDesk.Api.IntegrationTests;

// WebApplicationFactory arranca la API de verdad, con su middleware y su contenedor de DI, en memoria y sin abrir puertos.
// El ambiente "Testing" no carga User Secrets ni appsettings.Development.json: las pruebas no dependen de esta máquina.
public sealed class HelpDeskApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // La misma imagen de deploy/compose.yaml: lo que pasa aquí es lo que pasa en SQL Server de verdad.
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2025-latest";
    private const string Database = "HelpDesk";
    private const int MigrationAttempts = 10;

    private readonly MsSqlContainer sqlServer = new MsSqlBuilder(SqlServerImage)
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
        .Build();

    private readonly RabbitMqContainer rabbitMq = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    private readonly ConcurrentDictionary<string, Task<string>> tokens = new();

    // Generados en cada corrida: no hay nada que guardar en User Secrets, ni en el repositorio.
    private readonly string password = $"Prueba-{Guid.NewGuid():N}";
    private readonly string signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public string ConnectionString { get; private set; } = string.Empty;

    public TicketEventsListener Events { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(sqlServer.StartAsync(Test.Cancel), rabbitMq.StartAsync(Test.Cancel));

        // Antes de que arranque la API: sin una cola enlazada, el publicador no da nada por publicado.
        Events = await TicketEventsListener.StartAsync(rabbitMq.GetConnectionString(), Test.Cancel);

        ConnectionString = new SqlConnectionStringBuilder(sqlServer.GetConnectionString()) { InitialCatalog = Database }.ConnectionString;

        await MigrateWhenReadyAsync();

        await Services.SeedDevelopmentDataAsync(Test.Cancel);
    }

    // Las migraciones de verdad, desde cero: si una no funciona en una base vacía, se sabe aquí. Con reintentos porque un
    // SQL Server recién levantado puede aceptar una conexión y rechazar la siguiente mientras termina de arrancar.
    private async Task MigrateWhenReadyAsync()
    {
        await using var db = new HelpDeskDbContext(new DbContextOptionsBuilder<HelpDeskDbContext>().UseSqlServer(ConnectionString).Options);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(Test.Cancel);
                return;
            }
            catch (SqlException exception) when (attempt < MigrationAttempts)
            {
                TestContext.Current.SendDiagnosticMessage($"SQL Server aún no está listo (intento {attempt}): {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(3), Test.Cancel);
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:HelpDesk", ConnectionString);
        builder.UseSetting("ConnectionStrings:RabbitMq", rabbitMq.GetConnectionString());
        // Un segundo: /health/ready da Degraded si pasan tres vueltas sin terminar, y con las pruebas en paralelo una vuelta puede tardar.
        builder.UseSetting("Outbox:PublishInterval", "00:00:01");
        builder.UseSetting("Jwt:SigningKey", signingKey);
        builder.UseSetting(DevelopmentSeeder.PasswordKey, password);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");

        // Sin límites: las pruebas corren en paralelo, y comparten la IP. RateLimitTests revisa los límites por su cuenta.
        builder.UseSetting("RateLimiting:UserBurst", "10000");
        builder.UseSetting("RateLimiting:UserRequestsPerMinute", "60000");
        builder.UseSetting("RateLimiting:LoginAttemptsPerMinute", "1000");

        // El monitor corre una vez al arrancar, y no vuelve a molestar. SlaTests lo invoca cuando le hace falta. Con lotes
        // chicos, cualquier barrido con algunas alertas pasa por el camino de varios lotes.
        builder.UseSetting("SlaMonitor:CheckInterval", "01:00:00");
        builder.UseSetting("SlaMonitor:BatchSize", "7");
    }

    public ApiUser Anonymous() => new(CreateClient());

    public async Task<ApiUser> AsAsync(string user)
    {
        string token = await TokenOfAsync(user);

        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        return new ApiUser(client);
    }

    public Task<string> TokenOfAsync(string user) => tokens.GetOrAdd(user, IssueTokenAsync);

    public async Task<TokenResponse> LoginAsync(string user)
    {
        HttpResponseMessage response = await CreateClient().PostAsJsonAsync(
            "/api/token", new TokenRequest(EmailOf(user), password), Json.Options, Test.Cancel);

        return await response.ReadAsync<TokenResponse>();
    }

    public TokenRequest CredentialsOf(string user) => new(EmailOf(user), password);

    public async Task<List<OutboxMessage>> OutboxRowsAsync(int ticketId, string type)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        HelpDeskDbContext db = scope.ServiceProvider.GetRequiredService<HelpDeskDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.Type == type && message.Payload.Contains($"\"ticketId\":{ticketId},"))
            .ToListAsync(Test.Cancel);
    }

    public async Task<bool> IsPublishedAsync(Guid messageId)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        HelpDeskDbContext db = scope.ServiceProvider.GetRequiredService<HelpDeskDbContext>();

        return await db.OutboxMessages.AnyAsync(message => message.MessageId == messageId && message.PublishedAt != null, Test.Cancel);
    }

    // Para lo que la API no deja hacer: un ticket creado "hace horas", para las pruebas de SLA.
    public async Task<Ticket> InsertTicketAsync(
        string title,
        TicketPriority priority,
        DateTimeOffset createdAt,
        string? assignedTo = null,
        string? requestedBy = null)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        ITicketRepository repository = scope.ServiceProvider.GetRequiredService<ITicketRepository>();

        var ticket = new Ticket
        {
            Id = await repository.NextIdAsync(Test.Cancel),
            Title = title,
            Priority = priority,
            CreatedAt = createdAt,
            AssignedTo = AgentName.From(assignedTo),
            RequestedBy = requestedBy
        };

        await repository.AddAsync(ticket, Test.Cancel);

        return ticket;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Events.DisposeAsync();
        await rabbitMq.DisposeAsync();
        await sqlServer.DisposeAsync();
    }

    private async Task<string> IssueTokenAsync(string user) => (await LoginAsync(user)).AccessToken;

    private static string EmailOf(string user) =>
        user is "camila" or "diego" ? $"{user}@acme.local" : $"{user}@helpdesk.local";
}
