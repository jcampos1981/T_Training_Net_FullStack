using System.ComponentModel.DataAnnotations;
using HelpDesk.Ai;
using HelpDesk.ServiceDefaults;
using HelpDesk.Worker;
using Microsoft.Extensions.Diagnostics.HealthChecks;

if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync();
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddHelpDeskTelemetry("helpdesk-worker");

builder.Services.AddOptions<TicketEventsOptions>()
    .BindConfiguration(TicketEventsOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
builder.Services.AddSingleton<TicketEmailHandler>();

AddConsumer<TicketEmailsConsumer>(builder.Services);

// Sin IA configurada, el worker sigue enviando correos, y no se enlaza ninguna cola de embeddings que nadie vaya a leer.
if (AiSetup.IsEnabled(builder.Configuration))
{
    builder.Services.AddHelpDeskEmbeddings(builder.Configuration);
    builder.Services.AddSingleton<TicketEmbeddingHandler>();
    AddConsumer<TicketEmbeddingsConsumer>(builder.Services);
}

builder.Services.AddHealthChecks()
    .AddCheck<ConsumersHealthCheck>("rabbitmq", HealthStatus.Degraded, [HealthEndpoints.ReadyTag]);

WebApplication app = builder.Build();

app.MapHealthEndpoints();

app.Run();

return 0;

// También como singleton con su propio nombre: el health check les pregunta si están conectados.
static void AddConsumer<TConsumer>(IServiceCollection services)
    where TConsumer : TicketEventConsumer
{
    services.AddSingleton<TConsumer>();
    services.AddSingleton<TicketEventConsumer>(provider => provider.GetRequiredService<TConsumer>());
    services.AddHostedService(provider => provider.GetRequiredService<TConsumer>());
}

namespace HelpDesk.Worker
{
    public sealed class TicketEventsOptions
    {
        public const string SectionName = "TicketEvents";

        [Range(typeof(TimeSpan), "00:00:00.100", "01:00:00")]
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

        [Range(1, 20)]
        public int MaxAttempts { get; set; } = 4;

        // Cuántos mensajes le entrega RabbitMQ sin que haya confirmado los anteriores.
        [Range(1, 1000)]
        public int Prefetch { get; set; } = 10;
    }

    public sealed class ConsumersHealthCheck(IEnumerable<TicketEventConsumer> consumers) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(consumers.All(consumer => consumer.IsConnected)
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, "Sin conexión con RabbitMQ."));
    }
}
