using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace UserService.Infrastructure;

/// <summary>
/// Readiness check for PostgreSQL.
///
/// Two things it deliberately does not do:
///
/// It does not use the DbContext. A readiness probe that goes through EF Core's retry
/// policy waits up to 15 seconds before reporting failure, by which point the probe has
/// already timed out and the answer is useless.
///
/// It does not put the exception message in the response. The probe endpoint is
/// anonymous — it has to be, the platform has no token — so anything it returns is
/// public. A connection failure message contains the host, the database and the role.
/// The detail goes to the logs, where it is correlated by trace id anyway.
/// </summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource, ILogger<PostgresHealthCheck> logger)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Bounded independently of the caller: a hung connection attempt must not
            // hold the probe open until the platform gives up on it.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            await using var connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "PostgreSQL readiness check failed");
            return HealthCheckResult.Unhealthy("Database unreachable.");
        }
    }
}
