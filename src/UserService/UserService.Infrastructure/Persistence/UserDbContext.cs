using Microsoft.EntityFrameworkCore;
using UserService.Domain;

namespace UserService.Infrastructure.Persistence;

/// <summary>
/// There is no repository wrapping this, deliberately.
///
/// <see cref="DbContext"/> is already a unit of work and already a repository; wrapping
/// it in an IUserRepository adds a layer that can only ever expose a subset of LINQ,
/// loses query composition, and produces the familiar GetUserWithAddressesAndX method
/// explosion. The thing a repository is usually wanted for — testability — is better
/// served here by a real PostgreSQL container in tests, because the interesting bugs
/// (concurrency, constraints, provider translation) are exactly the ones an in-memory
/// fake cannot reproduce.
/// </summary>
public sealed class UserDbContext(DbContextOptions<UserDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("users");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UserDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Every DateTimeOffset maps to timestamptz. Npgsql is strict about this and will
        // throw at runtime on a DateTime with Unspecified kind — better to settle the
        // convention once here than to discover it per column.
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<string>().HaveMaxLength(256);
    }
}
