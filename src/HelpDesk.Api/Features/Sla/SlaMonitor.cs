using HelpDesk.Api.Background;
using HelpDesk.Api.Features.Notifications;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Features.Sla;

// Vigila los SLA en segundo plano mientras la API está corriendo. Las alertas quedan en la base antes de avisar: si el
// aviso en vivo se pierde (nadie conectado, una caída), siguen en /alertas.
public sealed class SlaMonitor(
    IServiceScopeFactory scopes,
    ITicketNotifier notifier,
    IOptions<SlaMonitorOptions> options,
    TimeProvider clock,
    ILogger<SlaMonitor> logger) : PeriodicBackgroundService(scopes, clock, logger)
{
    protected override TimeSpan Interval => options.Value.CheckInterval;

    protected override async Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        IReadOnlyList<RaisedSlaAlert> raised = await services.GetRequiredService<SlaAlertScanner>().ScanAsync(cancellationToken);

        if (raised.Count > 0)
        {
            Logger.LogInformation("SLA: {Count} alertas nuevas.", raised.Count);
            await notifier.SlaAlertsRaisedAsync(raised);
        }
    }
}
