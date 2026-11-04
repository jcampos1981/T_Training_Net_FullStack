using HelpDesk.Api.Errors;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HelpDesk.Api.UnitTests;

public sealed class SenderTests
{
    private readonly List<string> trace = [];

    [Fact]
    public async Task RunsTheHandlerInsideTheBehaviors_FirstRegisteredOutermost()
    {
        ISender sender = Build(services =>
        {
            services.AddSingleton<IPipelineBehavior<Ping, string>>(new RecordingBehavior("a", trace));
            services.AddSingleton<IPipelineBehavior<Ping, string>>(new RecordingBehavior("b", trace));
        });

        string response = await sender.SendAsync(new Ping(), Test.Cancel);

        Assert.Equal("pong", response);
        Assert.Equal(["a entra", "b entra", "handler", "b sale", "a sale"], trace);
    }

    [Fact]
    public async Task ABehaviorCanCutThePipelineShort()
    {
        ISender sender = Build(services => services.AddSingleton<IPipelineBehavior<Ping, string>>(new ShortCircuit()));

        Assert.Equal("cortado", await sender.SendAsync(new Ping(), Test.Cancel));
        Assert.Empty(trace);
    }

    [Fact]
    public async Task WithoutAHandler_ItFailsClearly()
    {
        ISender sender = new Sender(new ServiceCollection().BuildServiceProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new Ping(), Test.Cancel));
    }

    private ISender Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestHandler<Ping, string>>(new PingHandler(trace));
        configure(services);

        return new Sender(services.BuildServiceProvider());
    }

    private sealed record Ping : IRequest<string>;

    private sealed class PingHandler(List<string> trace) : IRequestHandler<Ping, string>
    {
        public Task<string> HandleAsync(Ping request, CancellationToken cancellationToken)
        {
            trace.Add("handler");
            return Task.FromResult("pong");
        }
    }

    private sealed class RecordingBehavior(string name, List<string> trace) : IPipelineBehavior<Ping, string>
    {
        public async Task<string> HandleAsync(Ping request, Func<Task<string>> next, CancellationToken cancellationToken)
        {
            trace.Add($"{name} entra");
            string response = await next();
            trace.Add($"{name} sale");
            return response;
        }
    }

    private sealed class ShortCircuit : IPipelineBehavior<Ping, string>
    {
        public Task<string> HandleAsync(Ping request, Func<Task<string>> next, CancellationToken cancellationToken) => Task.FromResult("cortado");
    }
}

public sealed class UseCaseLoggingBehaviorTests
{
    private readonly RecordingLogger logger = new();

    [Fact]
    public async Task ARejection_IsLogged_WithoutTheDetail()
    {
        var behavior = new UseCaseLoggingBehavior<Sample, Result<Unit>>(logger);
        Result<Unit> rejected = Error.BusinessRule("El ticket de camila@acme.local está cerrado");

        await behavior.HandleAsync(new Sample(), () => Task.FromResult(rejected), Test.Cancel);

        string line = Assert.Single(logger.Lines);
        Assert.Contains("Conflict", line);
        Assert.DoesNotContain("camila", line);
    }

    [Fact]
    public async Task ASuccess_IsNotLogged()
    {
        var behavior = new UseCaseLoggingBehavior<Sample, Result<Unit>>(logger);

        await behavior.HandleAsync(new Sample(), () => Task.FromResult<Result<Unit>>(new Unit()), Test.Cancel);

        Assert.Empty(logger.Lines);
    }

    public sealed record Sample : IRequest<Result<Unit>>;

    private sealed class RecordingLogger : ILogger<UseCaseLoggingBehavior<Sample, Result<Unit>>>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}

public sealed class ResultHttpExtensionsTests
{
    [Fact]
    public void Success_UsesWhatTheEndpointSays()
    {
        Result<int> result = 5;

        IResult http = result.ToHttp(value => TypedResults.Ok(value));

        Assert.Equal(5, Assert.IsType<Ok<int>>(http).Value);
    }

    [Fact]
    public void NotFound_IsA404() => Assert.IsType<NotFound>(Error.NotFound.ToHttp());

    [Fact]
    public void Forbidden_IsA403() => Assert.IsType<ForbidHttpResult>(Error.Forbidden.ToHttp());

    [Fact]
    public void Conflict_IsA409_WithTheExplanation()
    {
        var problem = Assert.IsType<ProblemHttpResult>(Error.BusinessRule("Está cerrado.").ToHttp());

        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal("Está cerrado.", problem.ProblemDetails.Detail);
    }
}
