using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UserService.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef migrations` at design time, never at runtime.
///
/// Without it, EF's tooling has to start the application host to find a DbContext, which
/// means generating a migration would need a reachable database, working Azure
/// credentials, and every option binding to succeed. Migrations are a source-code
/// operation; they should work offline on a laptop with no Azure login.
///
/// The connection string here is never connected to — EF only needs the provider to know
/// which SQL dialect to emit.
/// </summary>
internal sealed class UserDbContextFactory : IDesignTimeDbContextFactory<UserDbContext>
{
    public UserDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=userdb;Username=design_time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "users"))
            .Options;

        return new UserDbContext(options);
    }
}
