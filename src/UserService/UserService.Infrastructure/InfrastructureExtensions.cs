using Ecommerce.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using UserService.Infrastructure.Persistence;

namespace UserService.Infrastructure;

public static class InfrastructureExtensions
{
    public static IHostApplicationBuilder AddUserInfrastructure(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<PostgresOptions>()
            .Bind(builder.Configuration.GetSection(PostgresOptions.SectionName));

        var options = builder.Configuration.GetSection(PostgresOptions.SectionName).Get<PostgresOptions>()
                      ?? new PostgresOptions();

        // Present locally, absent in Azure. Its absence is what selects Entra token auth.
        var localConnectionString = builder.Configuration.GetConnectionString("UserService");

        // A single NpgsqlDataSource for the process: it owns the connection pool and the
        // token refresh timer. Registering it as a singleton is not an optimisation —
        // creating one per DbContext would create one pool and one refresh timer per
        // context, which is how you exhaust a server while looking idle.
        builder.Services.AddSingleton(sp =>
            NpgsqlDataSourceFactory.Create(
                options,
                localConnectionString,
                sp.GetRequiredService<ILoggerFactory>()));

        builder.Services.AddDbContextPool<UserDbContext>((sp, dbOptions) =>
        {
            dbOptions.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql =>
            {
                // Retries for transient faults: an Azure PostgreSQL failover takes tens
                // of seconds, and without this every in-flight request during a planned
                // maintenance window becomes a 500.
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);

                npgsql.MigrationsHistoryTable("__ef_migrations_history", "users");
            });

            if (builder.Environment.IsDevelopment())
            {
                // Parameter values in logs are a privacy incident in production and a
                // debugging necessity locally. Environment-gated, never configurable.
                dbOptions.EnableDetailedErrors();
                dbOptions.EnableSensitiveDataLogging();
            }
        });

        builder.Services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>(
                "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: [HealthExtensions.ReadyTag]);

        return builder;
    }
}
