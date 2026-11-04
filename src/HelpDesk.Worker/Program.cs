using System.ComponentModel.DataAnnotations;
using HelpDesk.ServiceDefaults;
using HelpDesk.Worker;
using Microsoft.Extensions.Diagnostics.HealthChecks;

if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync();
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddHelpDeskTelemetry("helpdesk-worker");

builder.Services.AddOptions<TicketEmailsOptions>()
    .BindConfiguration(TicketEmailsOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
builder.Services.AddSingleton<TicketEmailHandler>();

// También como singleton con su propio nombre: el health check le pregunta si está conectado.
builder.Services.AddSingleton<TicketEmailsConsumer>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TicketEmailsConsumer>());

builder.Services.AddHealthChecks()
    .AddCheck<ConsumerHealthCheck>("rabbitmq", HealthStatus.Degraded, [HealthEndpoints.ReadyTag]);

WebApplication app = builder.Build();

app.MapHealthEndpoints();

app.Run();

return 0;

namespace HelpDesk.Worker
{
    public sealed class TicketEmailsOptions
    {
        public const string SectionName = "TicketEmails";

        [Range(typeof(TimeSpan), "00:00:00.100", "01:00:00")]
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

        [Range(1, 20)]
        public int MaxAttempts { get; set; } = 4;

        // Cuántos mensajes le entrega RabbitMQ sin que haya confirmado los anteriores.
        [Range(1, 1000)]
        public int Prefetch { get; set; } = 10;
    }

    public sealed class ConsumerHealthCheck(TicketEmailsConsumer consumer) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(consumer.IsConnected
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, "Sin conexión con RabbitMQ."));
    }
}
