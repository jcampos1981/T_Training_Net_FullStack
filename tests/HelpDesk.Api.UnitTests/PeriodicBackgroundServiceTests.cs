using HelpDesk.Api.Background;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HelpDesk.Api.UnitTests;

// Con un reloj falso: el intervalo pasa cuando la prueba lo adelanta, sin esperar de verdad.
public sealed class PeriodicBackgroundServiceTests : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly FakeTimeProvider clock = new(Tickets.Now);
    private readonly List<object> scopedInstances = [];
    private TestJob? job;
    private int rounds;
    private bool failing;

    [Fact]
    public async Task RunsWhenStarting_AndOnEachInterval()
    {
        await StartAsync();
        await WaitForRoundsAsync(1);

        clock.Advance(Interval);
        await WaitForRoundsAsync(2);

        clock.Advance(Interval);
        await WaitForRoundsAsync(3);
    }

    // Sin esto, un corte de la base detendría toda la API.
    [Fact]
    public async Task AFailedRound_DoesNotStopTheJob()
    {
        failing = true;
        await StartAsync();
        await WaitForRoundsAsync(1);

        failing = false;
        clock.Advance(Interval);

        await WaitForRoundsAsync(2);
    }

    [Fact]
    public async Task EachRound_GetsItsOwnScope()
    {
        await StartAsync();
        await WaitForRoundsAsync(1);

        clock.Advance(Interval);
        await WaitForRoundsAsync(2);

        Assert.NotSame(scopedInstances[0], scopedInstances[1]);
    }

    [Fact]
    public async Task LastSuccess_OnlyMovesWhenARoundEndsWell()
    {
        await StartAsync();
        await WaitForRoundsAsync(1);
        await WaitUntilAsync(() => job!.LastSuccessAt == clock.GetUtcNow());

        failing = true;
        clock.Advance(Interval);
        await WaitForRoundsAsync(2);
        await Task.Delay(TimeSpan.FromMilliseconds(100), Test.Cancel);

        Assert.Equal(Tickets.Now, job!.LastSuccessAt);
    }

    [Fact]
    public async Task HealthCheck_IsHealthyWhileItKeepsFinishing_AndDegradedWhenItStops()
    {
        await StartAsync();
        await WaitForRoundsAsync(1);
        await WaitUntilAsync(() => job!.LastSuccessAt == clock.GetUtcNow());
        var check = new PeriodicJobHealthCheck<TestJob>(job!, clock);

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(check)).Status);

        failing = true;
        clock.Advance(Interval * 3);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(check)).Status);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(check)).Status);
    }

    public async ValueTask DisposeAsync()
    {
        if (job is not null)
        {
            await job.StopAsync(CancellationToken.None);
            job.Dispose();
        }
    }

    private Task<HealthCheckResult> CheckAsync(IHealthCheck check) => check.CheckHealthAsync(
        new HealthCheckContext { Registration = new HealthCheckRegistration("trabajo", check, HealthStatus.Degraded, null) }, Test.Cancel);

    private async Task StartAsync()
    {
        IServiceScopeFactory scopes = new ServiceCollection().AddScoped<object>().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        job = new TestJob(scopes, clock, services =>
        {
            scopedInstances.Add(services.GetRequiredService<object>());
            Interlocked.Increment(ref rounds);

            return failing ? throw new InvalidOperationException("La base no responde.") : Task.CompletedTask;
        });

        await job.StartAsync(Test.Cancel);
    }

    private Task WaitForRoundsAsync(int expected) => WaitUntilAsync(() => Volatile.Read(ref rounds) >= expected);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), Test.Cancel);
        }

        Assert.True(condition(), "El trabajo no llegó a ese estado.");
    }

    public sealed class TestJob(IServiceScopeFactory scopes, TimeProvider clock, Func<IServiceProvider, Task> run)
        : PeriodicBackgroundService(scopes, clock, NullLogger.Instance)
    {
        public override TimeSpan Interval => PeriodicBackgroundServiceTests.Interval;

        protected override Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken) => run(services);
    }
}
