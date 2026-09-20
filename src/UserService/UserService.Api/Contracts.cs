using System.ComponentModel.DataAnnotations;
using UserService.Domain;

namespace UserService.Api;

/// <summary>
/// Wire contracts. Separate from the domain entities on purpose: an aggregate's shape is
/// driven by its invariants, a response's shape by what a client needs. Serialising
/// entities directly couples the two, so the first refactor of a private field becomes a
/// breaking API change — and any property added to the entity is published to the world
/// by default, which is how internal flags and soft-delete timestamps leak.
/// </summary>
public sealed record UserResponse(
    Guid Id,
    Guid EntraObjectId,
    string? Email,
    string? DisplayName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AddressResponse> Addresses)
{
    public static UserResponse From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserResponse(
            user.Id,
            user.EntraObjectId,
            user.Email,
            user.DisplayName,
            user.CreatedAt,
            [.. user.Addresses.Select(AddressResponse.From)]);
    }
}

/// <summary>
/// The service-to-service view. Strictly narrower than <see cref="UserResponse"/>:
/// OrderService needs a display name and a shipping address, not an email address and
/// not every address on file. Least privilege applies to data shape, not just to RBAC.
/// </summary>
public sealed record UserSummaryResponse(Guid Id, string? DisplayName, AddressResponse? DefaultShippingAddress)
{
    public static UserSummaryResponse From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var shipping = user.Addresses.FirstOrDefault(a => a.IsDefaultShipping);
        return new UserSummaryResponse(user.Id, user.DisplayName, shipping is null ? null : AddressResponse.From(shipping));
    }
}

public sealed record AddressResponse(
    Guid Id,
    string? Label,
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string Country,
    bool IsDefaultShipping,
    bool IsDefaultBilling)
{
    public static AddressResponse From(Address address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return new AddressResponse(
            address.Id,
            address.Label,
            address.Line1,
            address.Line2,
            address.City,
            address.PostalCode,
            address.Country,
            address.IsDefaultShipping,
            address.IsDefaultBilling);
    }
}

public sealed record UpdateProfileRequest([property: MaxLength(256)] string? DisplayName);

/// <summary>
/// Validation attributes here are the cheap first pass — shape and length, rejected
/// before any database work. They are not the real rule: "country must be ISO 3166-1
/// alpha-2" lives in the domain, because that is a business rule and it must hold no
/// matter which code path creates an address.
/// </summary>
public sealed record AddressRequest(
    [property: MaxLength(64)] string? Label,
    [property: Required][property: MaxLength(256)] string Line1,
    [property: MaxLength(256)] string? Line2,
    [property: Required][property: MaxLength(128)] string City,
    [property: Required][property: MaxLength(32)] string PostalCode,
    [property: Required][property: MaxLength(2)] string Country,
    bool IsDefaultShipping = false,
    bool IsDefaultBilling = false);
