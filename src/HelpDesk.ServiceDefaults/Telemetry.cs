using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.ServiceDefaults;

// El nombre con que HelpDesk publica sus propias trazas y métricas.
public static class HelpDeskTelemetry
{
    public const string Name = "HelpDesk";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);
}

public static class TelemetrySetup
{
    private const string HealthPath = "/health";
    private const string OtlpEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";

    // Logs, métricas y trazas con OpenTelemetry, el estándar: no ata la aplicación a ninguna herramienta. Se exportan si hay un
    // colector configurado (Aspire define esa variable solo). Sin él, los logs siguen saliendo por la consola y nada más se envía.
    public static TBuilder AddHelpDeskTelemetry<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        OpenTelemetryBuilder telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(HelpDeskTelemetry.Name))
            .WithTracing(tracing => tracing
                .AddSource(HelpDeskTelemetry.Name)
                // Los health checks corren cada pocos segundos: taparían las trazas que importan.
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !IsHealthCheck(context.Request.Path))
                .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = request => !IsHealthCheck(request.RequestUri?.AbsolutePath))
                // Solo el SQL que es parte de algo. Los trabajos en segundo plano consultan cada pocos segundos, y cada SELECT suelto sería una
                // traza nueva que tapa a las demás.
                .AddSqlClientInstrumentation(options => options.Filter = _ => HasParent(Activity.Current)));

        if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpoint]))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }

    // El filtro corre cuando el span del SQL ya existe: Activity.Current es él mismo. Lo que importa es si tiene un padre.
    private static bool HasParent(Activity? span) => span is not null && span.ParentSpanId != default;

    private static bool IsHealthCheck(PathString path) => path.StartsWithSegments(HealthPath);

    private static bool IsHealthCheck(string? path) => path?.StartsWith(HealthPath, StringComparison.OrdinalIgnoreCase) ?? false;
}
