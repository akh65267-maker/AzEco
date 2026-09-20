using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using UserService.Infrastructure.Persistence;

namespace UserService.Tests.Integration;

/// <summary>
/// Boots the real application against a real PostgreSQL in a container.
///
/// WHY A CONTAINER AND NOT THE IN-MEMORY PROVIDER
/// Everything this service gets wrong is provider-specific: the unique-violation race in
/// JIT provisioning, the partial unique indexes enforcing "one default address", citext
/// case-insensitivity, timestamptz conversion. The in-memory provider has no constraints
/// at all, so a test suite built on it passes while the production database rejects the
/// same operation. That is worse than no test.
///
/// WHY NOT `docker compose up` FIRST
/// A test that depends on the developer having started the local stack fails differently
/// on every machine and in CI. Testcontainers owns the lifecycle: the container is
/// created for the fixture and destroyed with it.
/// </summary>
public sealed class UserApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>
    /// Must match Auth:LocalDevSigningKey below. In Development the service validates
    /// tokens against this symmetric key instead of Entra's JWKS, which is what lets the
    /// suite run with no tenant and no internet. That path is guarded by
    /// IsDevelopment() in AuthenticationExtensions — a symmetric key is a forgeable
    /// identity, so it must be impossible to reach in a deployed environment.
    /// </summary>
    public const string SigningKey = "integration-test-signing-key-at-least-32-bytes-long";

    public const string Issuer = "https://localhost/ecommerce-dev";
    public const string Audience = "api://ecommerce-test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("userdb")
        .WithUsername("user_app")
        .WithPassword("localdev")
        .Build();

    // Implemented explicitly: xunit's IAsyncLifetime.DisposeAsync returns Task, while
    // WebApplicationFactory already has a ValueTask DisposeAsync from IAsyncDisposable.
    // Two methods with the same name and different return types cannot both be implicit.
    async Task IAsyncLifetime.InitializeAsync()
    {
        await _postgres.StartAsync().ConfigureAwait(false);

        // Migrations run here, against a real server — which also means the migration
        // itself is tested on every CI run rather than discovered to be broken at deploy
        // time. EnsureCreated would skip them entirely and defeat that.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        // Host first, then the container: shutting the database down while the host
        // still holds pooled connections produces noisy, meaningless errors on teardown.
        await base.DisposeAsync().ConfigureAwait(false);
        await _postgres.DisposeAsync().ConfigureAwait(false);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Presence of a connection string is what selects password auth over the
                // managed-identity token path — the same switch the real service uses.
                ["ConnectionStrings:UserService"] = _postgres.GetConnectionString(),
                ["Auth:Audience"] = Audience,
                ["Auth:LocalDevIssuer"] = Issuer,
                ["Auth:LocalDevSigningKey"] = SigningKey,

                // No exporter configured: OpenTelemetry instrumentation still runs, the
                // telemetry simply goes nowhere. An exporter must never be a startup
                // hard-failure, and this asserts that by omission.
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = null,
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = null,
            });
        });
    }

    /// <summary>Resets state between tests without paying to recreate the container.</summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        // TRUNCATE ... CASCADE rather than deleting rows: it is fast, it resets in one
        // statement, and it does not depend on getting the delete order right as the
        // schema grows.
        await db.Database
            .ExecuteSqlRawAsync("TRUNCATE users.users, users.addresses RESTART IDENTITY CASCADE")
            .ConfigureAwait(false);
    }
}
