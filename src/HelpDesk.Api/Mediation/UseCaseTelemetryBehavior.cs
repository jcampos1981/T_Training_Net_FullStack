using System.Diagnostics;
using System.Diagnostics.Metrics;
using HelpDesk.Domain;
using HelpDesk.ServiceDefaults;

namespace HelpDesk.Api.Mediation;

// Cada caso de uso, medido sin que cada handler tenga que acordarse: una traza con su nombre, y cuánto tardó según cómo terminó.
// Así un dashboard responde "qué caso de uso está lento" y "cuál rechaza más", no solo "qué URL".
public sealed class UseCaseTelemetryBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private const string Success = "Success";
    private const string Failure = "Exception";

    private static readonly string UseCase = typeof(TRequest).DeclaringType?.Name ?? typeof(TRequest).Name;

    private static readonly Histogram<double> Duration = HelpDeskTelemetry.Meter.CreateHistogram<double>(
        "helpdesk.use_case.duration",
        unit: "ms",
        description: "Cuánto tarda cada caso de uso");

    public async Task<TResponse> HandleAsync(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        using Activity? span = HelpDeskTelemetry.Source.StartActivity(UseCase);
        long started = Stopwatch.GetTimestamp();
        string outcome = Failure;

        try
        {
            TResponse response = await next();
            outcome = response is IFallible { Failure: { } failure } ? failure.Kind.ToString() : Success;

            return response;
        }
        finally
        {
            span?.SetTag("helpdesk.use_case.outcome", outcome);
            if (outcome == Failure)
            {
                span?.SetStatus(ActivityStatusCode.Error);
            }

            Duration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("use_case", UseCase),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}
