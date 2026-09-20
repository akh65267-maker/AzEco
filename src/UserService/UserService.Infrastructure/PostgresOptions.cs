namespace UserService.Infrastructure;

/// <summary>
/// How to reach PostgreSQL. Bound from the "Postgres" configuration section.
///
/// Note what is NOT here: a password. In Azure the credential is an Entra access token
/// fetched through the service's managed identity and refreshed roughly hourly; locally
/// it is a password that lives in <c>ConnectionStrings:UserService</c> in
/// appsettings.Development.json, which is the only place a password is ever acceptable.
/// </summary>
public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    public string? Host { get; set; }

    public string? Database { get; set; }

    public int Port { get; set; } = 5432;

    /// <summary>
    /// The PostgreSQL role name. Under Entra authentication this is the managed
    /// identity's RESOURCE NAME (e.g. "id-user-dev7x3k9q") — that is how Azure maps a
    /// Postgres role to an Entra principal. Get it wrong and the login fails with a
    /// generic authentication error that names nothing.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Maximum pooled connections. Deliberately low and explicit.
    ///
    /// A Burstable B1ms server caps out around 50 connections total, shared by three
    /// services. EF Core's default pool size is 100 — per service. Leaving the default
    /// in place means the first real traffic spike exhausts the server, and connection
    /// exhaustion is the most likely genuine outage in this architecture: silent,
    /// gradual, and it looks like a slow application rather than a full one.
    /// </summary>
    public int MaxPoolSize { get; set; } = 20;

    /// <summary>
    /// True when running against Azure PostgreSQL through PgBouncer in transaction
    /// pooling mode, which is how the Bicep provisions it.
    ///
    /// Transaction pooling hands a different backend connection to each transaction, so
    /// anything session-scoped breaks: server-side prepared statements most of all.
    /// This switch turns them off. Forgetting it produces "prepared statement
    /// &quot;_p1&quot; already exists" under load and only under load.
    /// </summary>
    public bool UseTransactionPooling { get; set; }
}
