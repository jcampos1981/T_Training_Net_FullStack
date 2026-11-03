using HelpDesk.Api.Background;

namespace HelpDesk.Api.Features.Accounts.Tokens;

// Un refresh token vencido ya no sirve para nada, pero su fila se queda en la tabla para siempre. Este trabajo la limpia.
public sealed class ExpiredRefreshTokenCleanup(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<ExpiredRefreshTokenCleanup> logger) : PeriodicBackgroundService(scopes, clock, logger)
{
    public override TimeSpan Interval => TimeSpan.FromHours(6);

    protected override async Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        int deleted = await services.GetRequiredService<RefreshTokenStore>().DeleteExpiredAsync(cancellationToken);

        if (deleted > 0)
        {
            Logger.LogInformation("Refresh tokens vencidos eliminados: {Count}.", deleted);
        }
    }
}
