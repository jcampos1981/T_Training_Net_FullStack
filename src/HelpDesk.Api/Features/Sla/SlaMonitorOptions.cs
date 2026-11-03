using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Features.Sla;

public sealed class SlaMonitorOptions
{
    public const string SectionName = "SlaMonitor";

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    // Cuántas alertas se crean por sentencia. Lotes cortos: transacciones cortas, y la tabla no queda bloqueada.
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 1_000;
}
