using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HelpDesk.Api.Health;

public static class HealthDetailsWriter
{
    // Sin el texto de las excepciones: el de una conexión fallida puede traer el nombre del servidor. Ese detalle va al log.
    public static Task WriteAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(
            new
            {
                Status = report.Status.ToString(),
                TotalDurationMs = (int)report.TotalDuration.TotalMilliseconds,
                Checks = report.Entries.Select(entry => new
                {
                    Name = entry.Key,
                    Status = entry.Value.Status.ToString(),
                    DurationMs = (int)entry.Value.Duration.TotalMilliseconds,
                    Description = entry.Value.Exception is null ? entry.Value.Description : null
                })
            },
            JsonSerializerOptions.Web);
}
