using Dapper;
using HelpDesk.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HelpDesk.Api.Health;

// Una conexión propia, con su tiempo límite, y no la del DbContext. Con la base caída, abrirla tarda hasta 15 s y no atiende
// la cancelación: el timeout de la revisión no sirve de nada, y /health/ready tardaba 14,8 s en responder.
public sealed class DatabaseHealthCheck(HelpDeskDbContext db) : IHealthCheck
{
    private const int TimeoutSeconds = 2;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = new SqlConnectionStringBuilder(db.Database.GetConnectionString())
        {
            ConnectTimeout = TimeoutSeconds,
            ConnectRetryCount = 0
        };

        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", commandTimeout: TimeoutSeconds, cancellationToken: cancellationToken));

        return HealthCheckResult.Healthy();
    }
}
