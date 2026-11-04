using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;
using HelpDesk.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.Api.UnitTests;

// Los dos comparten una fuente estática: si corrieran en paralelo, cada una vería lo de la otra.
[CollectionDefinition(nameof(TelemetryCollection), DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection(nameof(TelemetryCollection))]
public sealed class UseCaseTelemetryBehaviorTests : IDisposable
{
    private readonly List<Activity> spans = [];
    private readonly List<(double Value, string? UseCase, string? Outcome)> measures = [];
    private readonly ActivityListener spanListener;
    private readonly MeterListener meterListener = new();

    public UseCaseTelemetryBehaviorTests()
    {
        spanListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HelpDeskTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(spanListener);

        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "helpdesk.use_case.duration")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, tags, _) => measures.Add((value, TagOf(tags, "use_case"), TagOf(tags, "outcome"))));
        meterListener.Start();
    }

    [Fact]
    public async Task ASuccess_IsMeasured_WithTheNameOfTheUseCase()
    {
        var behavior = new UseCaseTelemetryBehavior<UseCaseSample.Query, Result<Unit>>();

        await behavior.HandleAsync(new UseCaseSample.Query(), () => Task.FromResult<Result<Unit>>(new Unit()), Test.Cancel);

        Activity span = Assert.Single(spans);
        Assert.Equal(nameof(UseCaseSample), span.OperationName);
        Assert.Equal("Success", span.GetTagItem("helpdesk.use_case.outcome"));
        Assert.Contains(measures, measure => measure is { UseCase: nameof(UseCaseSample), Outcome: "Success" });
    }

    // Un rechazo es un resultado normal, no un error: no debe teñir la traza de rojo. Pero sí hay que poder contarlo.
    [Fact]
    public async Task ARejection_IsCountedByItsKind_AndIsNotAnError()
    {
        var behavior = new UseCaseTelemetryBehavior<UseCaseSample.Query, Result<Unit>>();

        await behavior.HandleAsync(new UseCaseSample.Query(), () => Task.FromResult<Result<Unit>>(Error.NotFound), Test.Cancel);

        Activity span = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Contains(measures, measure => measure.Outcome == nameof(ErrorKind.NotFound));
    }

    [Fact]
    public async Task AnException_MarksTheTraceAsAnError_AndContinues()
    {
        var behavior = new UseCaseTelemetryBehavior<UseCaseSample.Query, Result<Unit>>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            behavior.HandleAsync(new UseCaseSample.Query(), () => throw new InvalidOperationException(), Test.Cancel));

        Activity span = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Contains(measures, measure => measure.Outcome == "Exception");
    }

    public void Dispose()
    {
        spanListener.Dispose();
        meterListener.Dispose();
    }

    private static string? TagOf(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == name)
            {
                return tag.Value as string;
            }
        }

        return null;
    }
}

public static class UseCaseSample
{
    public sealed record Query : IRequest<Result<Unit>>;
}

public sealed class ResilienceSetupTests
{
    private const string Client = "prueba";

    [Fact]
    public async Task AGet_IsRetried_UntilItWorks()
    {
        var server = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        using HttpResponseMessage response = await ClientFor(server).GetAsync("http://api/tickets", Test.Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, server.Calls);
    }

    // Quizá el primero sí se procesó, y repetirlo crearía un ticket duplicado.
    [Fact]
    public async Task APost_IsNotRetried()
    {
        var server = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        using HttpResponseMessage response = await ClientFor(server).PostAsync("http://api/tickets", null, Test.Cancel);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task AClientError_IsNotRetried()
    {
        var server = new ScriptedHandler(HttpStatusCode.NotFound, HttpStatusCode.OK);

        using HttpResponseMessage response = await ClientFor(server).GetAsync("http://api/tickets/1", Test.Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, server.Calls);
    }

    private static HttpClient ClientFor(ScriptedHandler server)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(Client)
            .AddHelpDeskResilience(retryDelay: TimeSpan.FromMilliseconds(1))
            .ConfigurePrimaryHttpMessageHandler(() => server);

        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(Client);
    }

    private sealed class ScriptedHandler(params HttpStatusCode[] responses) : HttpMessageHandler
    {
        private int calls;

        public int Calls => calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref calls);

            return Task.FromResult(new HttpResponseMessage(responses[Math.Min(call, responses.Length) - 1]));
        }
    }
}
