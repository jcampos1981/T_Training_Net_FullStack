using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HelpDesk.Api.Background;

// ¿El trabajo sigue terminando sus vueltas? Un BackgroundService que dejó de funcionar no avisa a nadie: la API responde
// normal, y el problema se nota cuando alguien pregunta por qué ya no llegan las alertas.
public sealed class PeriodicJobHealthCheck<TJob>(TJob job, TimeProvider clock) : IHealthCheck
    where TJob : PeriodicBackgroundService
{
    // Una vuelta fallida puede ser un corte de segundos. Varias seguidas ya no.
    private const int RoundsMissedAllowed = 3;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        TimeSpan silence = clock.GetUtcNow() - job.LastSuccessAt;

        return Task.FromResult(silence <= job.Interval * RoundsMissedAllowed
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, $"Sin terminar una vuelta hace {silence.TotalSeconds:N0} s."));
    }
}
