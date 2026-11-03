namespace HelpDesk.Api.Background;

// Patrón Template Method: aquí está lo que necesita todo trabajo periódico, y cada trabajo solo dice qué hace y cada
// cuánto. Se ejecuta una vez al arrancar la aplicación y después en cada intervalo.
public abstract class PeriodicBackgroundService(IServiceScopeFactory scopes, TimeProvider clock, ILogger logger) : BackgroundService
{
    // En ticks: lo escribe este trabajo y lo lee otro hilo, y un DateTimeOffset no se puede leer ni escribir de una sola vez.
    private long lastSuccessTicks = clock.GetUtcNow().UtcTicks;

    protected ILogger Logger { get; } = logger;

    public abstract TimeSpan Interval { get; }

    // Cuándo terminó bien su última vuelta, o cuándo se creó si todavía no termina ninguna.
    public DateTimeOffset LastSuccessAt => new(Interlocked.Read(ref lastSuccessTicks), TimeSpan.Zero);

    // Un BackgroundService es singleton, y el DbContext es Scoped: pedirlo en el constructor dejaría uno solo, vivo para
    // siempre, con su caché y su conexión. Por eso cada vuelta recibe un scope nuevo, como si fuera una petición HTTP.
    protected abstract Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer no se solapa: si una vuelta tarda más que el intervalo, la siguiente espera. Un System.Timers.Timer
        // dispararía otra en paralelo.
        using var timer = new PeriodicTimer(Interval, clock);

        try
        {
            do
            {
                await RunSafelyAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // La aplicación se está cerrando.
        }
    }

    private async Task RunSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            Interlocked.Exchange(ref lastSuccessTicks, clock.GetUtcNow().UtcTicks);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Sin esto, un corte de la base detendría TODA la API: desde .NET 6, una excepción sin atrapar en un
            // BackgroundService cierra la aplicación (BackgroundServiceExceptionBehavior.StopHost).
            Logger.LogError(exception, "{Job} falló; se vuelve a intentar en {Interval}.", GetType().Name, Interval);
        }
    }
}
