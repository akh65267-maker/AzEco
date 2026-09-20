namespace UserService.Domain;

/// <summary>
/// The application user — what this system knows about a person, as distinct from who
/// Entra ID says they are.
///
/// THE SPLIT (docs/architecture.md §5, Q4)
///   Entra ID owns authentication: credentials, MFA, session, token issuance.
///   This aggregate owns everything commercial: profile, addresses, preferences, consent.
///
/// Why <see cref="Id"/> is ours and <see cref="EntraObjectId"/> is merely unique:
///   Using the Entra `oid` as the primary key would couple every foreign key in the
///   system to the identity provider. Add B2C, a social login, or a second tenant later
///   and you are migrating every FK in every service. With a local id, that change is
///   one extra row in a future user_identities table. Cheap insurance, taken on day one.
///
/// Why <see cref="Email"/> is a copy and not authoritative:
///   Entra is the source of truth. This copy exists so that listing orders does not
///   require a Graph call per row. It goes stale, and that is acceptable — it is
///   refreshed from token claims on each sign-in. Never authorize on it; authorize on
///   <see cref="EntraObjectId"/>, which is immutable.
/// </summary>
public sealed class User
{
    private readonly List<Address> _addresses = [];

    private User()
    {
        // EF Core materialisation.
    }

    private User(Guid id, Guid entraObjectId, Guid entraTenantId, string? email, string? displayName, DateTimeOffset now)
    {
        Id = id;
        EntraObjectId = entraObjectId;
        EntraTenantId = entraTenantId;
        Email = Normalize(email);
        DisplayName = displayName?.Trim();
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Our key. Every foreign key in the system points here, never at Entra.</summary>
    public Guid Id { get; private set; }

    /// <summary>The Entra `oid` claim: immutable, per-tenant, and the only safe join key.</summary>
    public Guid EntraObjectId { get; private set; }

    /// <summary>The `tid` claim. Stored so a future multi-tenant split does not need a backfill.</summary>
    public Guid EntraTenantId { get; private set; }

    /// <summary>A stale-tolerant copy of the token's email claim. Never authoritative.</summary>
    public string? Email { get; private set; }

    public string? DisplayName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Set when the user exercises their right to erasure. The row survives because
    /// OrderService holds foreign keys to it and financial records carry statutory
    /// retention that outranks erasure — so we redact rather than delete.
    /// </summary>
    public DateTimeOffset? AnonymizedAt { get; private set; }

    public bool IsAnonymized => AnonymizedAt is not null;

    public IReadOnlyCollection<Address> Addresses => _addresses.AsReadOnly();

    /// <summary>
    /// Creates the local row on a user's first authenticated request (just-in-time
    /// provisioning). There is deliberately no registration endpoint: Entra already
    /// performed the signup, and a second one would be a second source of truth about
    /// who exists.
    /// </summary>
    public static User ProvisionFromToken(
        Guid entraObjectId,
        Guid entraTenantId,
        string? email,
        string? displayName,
        DateTimeOffset now)
    {
        if (entraObjectId == Guid.Empty)
        {
            throw new DomainException("Entra object id is required.");
        }

        return new User(Guid.CreateVersion7(), entraObjectId, entraTenantId, email, displayName, now);
    }

    /// <summary>
    /// Refreshes the cached copies of token claims. Called on every request that
    /// resolves the user, but only writes when something actually changed — otherwise
    /// every read becomes a write, and the UPDATE storm shows up as inexplicable
    /// database load long before anyone suspects the profile endpoint.
    /// </summary>
    public bool RefreshFromToken(string? email, string? displayName, DateTimeOffset now)
    {
        if (IsAnonymized)
        {
            // Re-populating an erased user from a token would silently undo a GDPR
            // erasure the next time they signed in.
            return false;
        }

        var normalizedEmail = Normalize(email);
        var normalizedName = displayName?.Trim();

        if (normalizedEmail == Email && normalizedName == DisplayName)
        {
            return false;
        }

        Email = normalizedEmail ?? Email;
        DisplayName = normalizedName ?? DisplayName;
        UpdatedAt = now;
        return true;
    }

    public void UpdateProfile(string? displayName, DateTimeOffset now)
    {
        EnsureNotAnonymized();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        UpdatedAt = now;
    }

    public Address AddAddress(
        string? label,
        string line1,
        string? line2,
        string city,
        string postalCode,
        string country,
        bool isDefaultShipping,
        bool isDefaultBilling,
        DateTimeOffset now)
    {
        EnsureNotAnonymized();

        // A cap, because "addresses" is an unbounded user-controlled collection and
        // every unbounded collection is a denial-of-service waiting for someone bored.
        if (_addresses.Count >= MaxAddresses)
        {
            throw new DomainException($"A user may have at most {MaxAddresses} addresses.");
        }

        var address = new Address(Guid.CreateVersion7(), label ?? string.Empty, line1, line2, city, postalCode, country);
        _addresses.Add(address);

        // The first address is always both defaults. Otherwise a user with exactly one
        // address has no default, and checkout has to special-case "no default but
        // exactly one exists" — a branch that is always written late and wrong.
        ApplyDefaults(address, isDefaultShipping || _addresses.Count == 1, isDefaultBilling || _addresses.Count == 1);

        UpdatedAt = now;
        return address;
    }

    public void UpdateAddress(
        Guid addressId,
        string? label,
        string line1,
        string? line2,
        string city,
        string postalCode,
        string country,
        bool isDefaultShipping,
        bool isDefaultBilling,
        DateTimeOffset now)
    {
        EnsureNotAnonymized();

        var address = FindAddress(addressId);
        address.Update(label, line1, line2, city, postalCode, country);
        ApplyDefaults(address, isDefaultShipping, isDefaultBilling);
        UpdatedAt = now;
    }

    public void RemoveAddress(Guid addressId, DateTimeOffset now)
    {
        EnsureNotAnonymized();

        var address = FindAddress(addressId);
        _addresses.Remove(address);

        // Removing the default must promote a replacement, or the invariant "if any
        // address exists, exactly one is default" quietly stops holding.
        if (address.IsDefaultShipping && _addresses.Count > 0)
        {
            _addresses[0].SetDefaultShipping(true);
        }

        if (address.IsDefaultBilling && _addresses.Count > 0)
        {
            _addresses[0].SetDefaultBilling(true);
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// Right-to-erasure. Redacts personal data in place rather than deleting the row:
    /// OrderService holds foreign keys here, and deleting would either cascade into
    /// financial records we are legally required to retain or leave dangling references.
    /// </summary>
    public void Anonymize(DateTimeOffset now)
    {
        if (IsAnonymized)
        {
            return;
        }

        Email = null;
        DisplayName = null;

        foreach (var address in _addresses)
        {
            address.Anonymize();
        }

        AnonymizedAt = now;
        UpdatedAt = now;
    }

    public const int MaxAddresses = 20;

    private Address FindAddress(Guid addressId) =>
        _addresses.Find(a => a.Id == addressId)
        ?? throw new DomainException($"Address {addressId} does not belong to this user.");

    // "At most one default" is an invariant over the whole collection, which is exactly
    // why it lives on the aggregate root and Address.SetDefault* is internal. Enforcing
    // it with a database unique index instead would work, but the failure would surface
    // as a constraint violation at SaveChanges, far from the code that caused it.
    private void ApplyDefaults(Address address, bool isDefaultShipping, bool isDefaultBilling)
    {
        if (isDefaultShipping)
        {
            foreach (var other in _addresses)
            {
                other.SetDefaultShipping(false);
            }

            address.SetDefaultShipping(true);
        }

        if (isDefaultBilling)
        {
            foreach (var other in _addresses)
            {
                other.SetDefaultBilling(false);
            }

            address.SetDefaultBilling(true);
        }
    }

    private void EnsureNotAnonymized()
    {
        if (IsAnonymized)
        {
            throw new DomainException("This user has been anonymized and cannot be modified.");
        }
    }

    private static string? Normalize(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
}
