namespace UserService.Domain;

/// <summary>
/// A postal address belonging to a <see cref="User"/>.
///
/// An entity rather than a value object: it has its own identity, is referenced by id
/// from the API, and is updated in place. (A shipping address *snapshot* copied onto an
/// order is a different thing entirely — that one is an immutable value, and it lives in
/// OrderService, because an order must remain readable after the user deletes the
/// address it was shipped to.)
///
/// Mutations go through methods rather than public setters so the aggregate can keep its
/// invariants. There is no public constructor: addresses are created through
/// <see cref="User.AddAddress"/>, which is the only place that can enforce them.
/// </summary>
public sealed class Address
{
    // EF Core materialisation constructor. Private so application code cannot use it.
    private Address()
    {
        Line1 = string.Empty;
        City = string.Empty;
        PostalCode = string.Empty;
        Country = string.Empty;
    }

    internal Address(Guid id, string label, string line1, string? line2, string city, string postalCode, string country)
    {
        Id = id;
        Label = label;
        Line1 = Require(line1, nameof(line1));
        Line2 = line2;
        City = Require(city, nameof(city));
        PostalCode = Require(postalCode, nameof(postalCode));
        Country = NormalizeCountry(country);
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>User-facing name: "Home", "Office". Not a business key.</summary>
    public string? Label { get; private set; }

    public string Line1 { get; private set; }

    public string? Line2 { get; private set; }

    public string City { get; private set; }

    public string PostalCode { get; private set; }

    /// <summary>ISO 3166-1 alpha-2, upper-cased.</summary>
    public string Country { get; private set; }

    public bool IsDefaultShipping { get; private set; }

    public bool IsDefaultBilling { get; private set; }

    public void Update(string? label, string line1, string? line2, string city, string postalCode, string country)
    {
        Label = label;
        Line1 = Require(line1, nameof(line1));
        Line2 = line2;
        City = Require(city, nameof(city));
        PostalCode = Require(postalCode, nameof(postalCode));
        Country = NormalizeCountry(country);
    }

    // internal: only the User aggregate root may flip these, because "exactly one
    // default" is an invariant across the whole collection, not a property of one row.
    internal void SetDefaultShipping(bool value) => IsDefaultShipping = value;

    internal void SetDefaultBilling(bool value) => IsDefaultBilling = value;

    internal void Anonymize()
    {
        Label = null;
        Line1 = "[redacted]";
        Line2 = null;
        City = "[redacted]";
        PostalCode = "[redacted]";
    }

    private static string Require(string value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainException($"Address {field} is required.")
            : value.Trim();

    private static string NormalizeCountry(string country)
    {
        var trimmed = Require(country, nameof(country));

        // Two letters, so 'Germany' and 'DEU' are rejected rather than silently stored
        // alongside 'DE' and breaking every shipping-rate lookup later.
        return trimmed.Length == 2
            ? trimmed.ToUpperInvariant()
            : throw new DomainException("Country must be an ISO 3166-1 alpha-2 code, e.g. 'DE'.");
    }
}
