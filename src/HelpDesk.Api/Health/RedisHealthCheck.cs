using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HelpDesk.Api.Health;

// Sin conexión, la lectura lanza una excepción, y el sistema de health checks la convierte en el estado de falla registrado.
public sealed class RedisHealthCheck(IDistributedCache cache) : IHealthCheck
{
    private const string ProbeKey = "health-probe";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await cache.GetAsync(ProbeKey, cancellationToken);

        return HealthCheckResult.Healthy();
    }
}
