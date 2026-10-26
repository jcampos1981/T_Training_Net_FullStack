using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Data;

public sealed class SlowQueryOptions
{
    public const string SectionName = "SlowQueries";

    // Desde cuántos milisegundos una consulta se considera lenta. Se lee en cada consulta (IOptionsMonitor),
    // así que se puede cambiar en appsettings sin reiniciar la API.
    [Range(1, int.MaxValue, ErrorMessage = "SlowQueries:ThresholdMilliseconds debe ser mayor que 0.")]
    public int ThresholdMilliseconds { get; set; } = 500;
}
