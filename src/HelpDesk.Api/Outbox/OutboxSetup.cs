using System.ComponentModel.DataAnnotations;
using HelpDesk.Api.Background;
using HelpDesk.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(5);

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 100;

    // Lo ya publicado se guarda unos días para investigar un problema. Trae correos de clientes, así que no se guarda para siempre.
    [Range(typeof(TimeSpan), "01:00:00", "90.00:00:00")]
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

public static class OutboxSetup
{
    private const string ConnectionName = "RabbitMq";

    // Sin RabbitMQ configurado, la API funciona igual y no escribe eventos: nadie los publicaría, y la tabla crecería sin límite.
    public static bool IsEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionName));

    public static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            return services;
        }

        services.AddOptions<OutboxOptions>()
            .BindConfiguration(OutboxOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<OutboxInterceptor>();
        services.AddSingleton(new RabbitMqPublisherChannel(configuration.GetConnectionString(ConnectionName)!));

        // También como singleton con su propio nombre: el health check le pregunta cuándo terminó su última vuelta.
        services.AddSingleton<OutboxPublisher>();
        services.AddHostedService(provider => provider.GetRequiredService<OutboxPublisher>());
        services.AddHostedService<OutboxCleanup>();

        return services;
    }
}

public sealed class OutboxCleanup(
    IServiceScopeFactory scopes,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxCleanup> logger) : PeriodicBackgroundService(scopes, clock, logger)
{
    private readonly TimeProvider clock = clock;

    public override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        DateTimeOffset limit = clock.GetUtcNow() - options.Value.Retention;
        int deleted = await services.GetRequiredService<HelpDeskDbContext>().OutboxMessages
            .Where(message => message.PublishedAt < limit)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            Logger.LogInformation("Eventos publicados eliminados del outbox: {Count}.", deleted);
        }
    }
}
