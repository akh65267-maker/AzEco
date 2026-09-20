using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace UserService.Infrastructure;

/// <summary>
/// Builds the Npgsql data source. This is the one class that differs between local
/// development and Azure, and it is deliberately the ONLY one — everything above it
/// (DbContext, endpoints, domain) is identical in both.
///
/// LOCAL   a connection string with a password, from appsettings.Development.json.
/// AZURE   no password exists at all. The Bicep provisions the server with
///         passwordAuth disabled, so the credential is an Entra access token fetched
///         through the service's user-assigned managed identity.
///
/// The token lives about an hour, which is the interesting part: a connection string is
/// evaluated once at startup, but a token is not valid for the life of the process.
/// Npgsql's periodic password provider exists for exactly this, and using the naive
/// approach instead — fetch a token once, put it in the connection string — produces a
/// service that works perfectly for an hour and then fails to open any new connection,
/// usually overnight, with an authentication error that suggests a configuration problem.
/// </summary>
public static class NpgsqlDataSourceFactory
{
    /// <summary>The scope Azure Database for PostgreSQL validates access tokens against.</summary>
    private const string PostgresScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>
    /// How long before expiry to refresh. Tokens last ~60 minutes; refreshing at 45
    /// leaves a wide margin for a slow Entra response without ever serving an expired one.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(45);

    /// <summary>If a refresh fails, retry this often rather than hammering Entra.</summary>
    private static readonly TimeSpan FailureRefreshInterval = TimeSpan.FromSeconds(30);

    public static NpgsqlDataSource Create(
        PostgresOptions options,
        string? localConnectionString,
        ILoggerFactory loggerFactory,
        TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var builder = string.IsNullOrWhiteSpace(localConnectionString)
            ? CreateAzureBuilder(options, credential ?? CreateCredential())
            : new NpgsqlDataSourceBuilder(localConnectionString);

        ApplyPoolingSettings(builder, options);

        builder.UseLoggerFactory(loggerFactory);

        // Emits the Npgsql ActivitySource that ObservabilityExtensions registers, so
        // every query becomes a child span of the request that issued it.
        builder.EnableParameterLogging(false);

        return builder.Build();
    }

    private static NpgsqlDataSourceBuilder CreateAzureBuilder(PostgresOptions options, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(options.Host) ||
            string.IsNullOrWhiteSpace(options.Database) ||
            string.IsNullOrWhiteSpace(options.Username))
        {
            throw new InvalidOperationException(
                "Postgres:Host, Postgres:Database and Postgres:Username are required when no local " +
                "connection string is configured. Postgres:Username must be the managed identity's " +
                "resource name, e.g. 'id-user-dev7x3k9q'.");
        }

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Database,
            Username = options.Username,
            SslMode = SslMode.Require,
        };

        var builder = new NpgsqlDataSourceBuilder(connectionString.ConnectionString);

        builder.UsePeriodicPasswordProvider(
            async (_, cancellationToken) =>
            {
                var token = await credential
                    .GetTokenAsync(new TokenRequestContext([PostgresScope]), cancellationToken)
                    .ConfigureAwait(false);

                return token.Token;
            },
            RefreshInterval,
            FailureRefreshInterval);

        return builder;
    }

    private static void ApplyPoolingSettings(NpgsqlDataSourceBuilder builder, PostgresOptions options)
    {
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(builder.ConnectionStringBuilder.ConnectionString)
        {
            // Explicit and low. EF Core's default of 100 per service, times three
            // services, against a Burstable server that caps near 50, is connection
            // exhaustion waiting for its first traffic spike.
            MaxPoolSize = options.MaxPoolSize,
            MinPoolSize = 1,

            // Fail fast. A 15-second default timeout means a saturated pool shows up as
            // a slow application rather than a failing one, and the readiness probe
            // keeps passing while every request piles up behind it.
            Timeout = 5,
            CommandTimeout = 30,
        };

        if (options.UseTransactionPooling)
        {
            // PgBouncer in transaction mode gives each transaction a different backend
            // connection, so nothing session-scoped survives. Server-side prepared
            // statements are the main casualty: leaving them on produces
            // 'prepared statement "_p1" already exists' under load, and only under load.
            connectionStringBuilder.MaxAutoPrepare = 0;
            connectionStringBuilder.NoResetOnClose = true;
        }

        builder.ConnectionStringBuilder.ConnectionString = connectionStringBuilder.ConnectionString;
    }

    private static DefaultAzureCredential CreateCredential() =>
        // Same code path everywhere: az-login locally, the user-assigned managed identity
        // in Azure. AZURE_CLIENT_ID (set by the Bicep) is what selects WHICH identity —
        // without it a container with one identity usually still works, then breaks the
        // moment a second is assigned.
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
        });
}
