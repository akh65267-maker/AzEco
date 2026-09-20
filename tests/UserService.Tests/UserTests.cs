using UserService.Domain;

namespace UserService.Tests;

/// <summary>
/// Pure domain tests: no database, no host, no mocks. They run in milliseconds because
/// the aggregate has no infrastructure dependencies — which is the practical payoff of
/// keeping EF Core, Npgsql and ASP.NET out of the Domain project, and what the
/// architecture test in OrderService.Tests enforces.
/// </summary>
public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ObjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static User NewUser() =>
        User.ProvisionFromToken(ObjectId, TenantId, "Ada@Example.com", " Ada Lovelace ", Now);

    [Fact]
    public void Provisioning_normalizes_claims_and_assigns_a_local_id()
    {
        var user = NewUser();

        // The local id is ours, not Entra's: every foreign key in the system points here,
        // so that adding a second identity provider later is one new row rather than a
        // migration of every FK in every service.
        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.NotEqual(user.EntraObjectId, user.Id);

        Assert.Equal(ObjectId, user.EntraObjectId);
        Assert.Equal("ada@example.com", user.Email);
        Assert.Equal("Ada Lovelace", user.DisplayName);
    }

    [Fact]
    public void Provisioning_without_an_object_id_is_rejected()
    {
        var ex = Assert.Throws<DomainException>(() =>
            User.ProvisionFromToken(Guid.Empty, TenantId, null, null, Now));

        Assert.Contains("object id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshFromToken_reports_no_change_when_claims_match()
    {
        var user = NewUser();

        // The whole point: an unchanged token must not produce a write. Returning true
        // here would turn every authenticated read into an UPDATE, and the resulting
        // load looks like a database problem rather than an application one.
        Assert.False(user.RefreshFromToken("ada@example.com", "Ada Lovelace", Now.AddHours(1)));
        Assert.Equal(Now, user.UpdatedAt);
    }

    [Fact]
    public void RefreshFromToken_reports_a_change_when_a_claim_differs()
    {
        var user = NewUser();
        var later = Now.AddHours(1);

        Assert.True(user.RefreshFromToken("ada@example.com", "Ada King", later));
        Assert.Equal("Ada King", user.DisplayName);
        Assert.Equal(later, user.UpdatedAt);
    }

    [Fact]
    public void First_address_becomes_default_for_both_shipping_and_billing()
    {
        var user = NewUser();

        // Requested as neither default — but it is the only address, so it must be both.
        // Otherwise checkout needs a "no default but exactly one exists" branch, and
        // that branch is always written late and wrong.
        var address = user.AddAddress("Home", "1 Main St", null, "Berlin", "10115", "de", false, false, Now);

        Assert.True(address.IsDefaultShipping);
        Assert.True(address.IsDefaultBilling);
        Assert.Equal("DE", address.Country);
    }

    [Fact]
    public void Setting_a_new_default_clears_the_previous_one()
    {
        var user = NewUser();
        var first = user.AddAddress("Home", "1 Main St", null, "Berlin", "10115", "DE", true, true, Now);
        var second = user.AddAddress("Office", "2 Side St", null, "Berlin", "10117", "DE", true, false, Now);

        Assert.False(first.IsDefaultShipping);
        Assert.True(second.IsDefaultShipping);

        // Billing was not requested on the second address, so it stays where it was.
        Assert.True(first.IsDefaultBilling);
        Assert.False(second.IsDefaultBilling);
    }

    [Fact]
    public void Removing_the_default_address_promotes_another()
    {
        var user = NewUser();
        var first = user.AddAddress("Home", "1 Main St", null, "Berlin", "10115", "DE", true, true, Now);
        user.AddAddress("Office", "2 Side St", null, "Berlin", "10117", "DE", false, false, Now);

        user.RemoveAddress(first.Id, Now);

        // "If any address exists, exactly one is default" has to keep holding after a
        // delete, or the invariant silently stops being true for existing users only.
        var remaining = Assert.Single(user.Addresses);
        Assert.True(remaining.IsDefaultShipping);
        Assert.True(remaining.IsDefaultBilling);
    }

    [Fact]
    public void An_address_belonging_to_someone_else_cannot_be_touched()
    {
        var user = NewUser();
        user.AddAddress("Home", "1 Main St", null, "Berlin", "10115", "DE", true, true, Now);

        // Structural authorization: the aggregate only ever searches its own collection,
        // so "update someone else's address" is not a check that can be forgotten — it
        // is a code path that does not exist.
        Assert.Throws<DomainException>(() => user.RemoveAddress(Guid.NewGuid(), Now));
    }

    [Theory]
    [InlineData("Germany")]
    [InlineData("DEU")]
    [InlineData("")]
    public void Country_must_be_an_iso_alpha2_code(string country)
    {
        var user = NewUser();

        Assert.Throws<DomainException>(() =>
            user.AddAddress("Home", "1 Main St", null, "Berlin", "10115", country, false, false, Now));
    }

    [Fact]
    public void Address_count_is_capped()
    {
        var user = NewUser();

        for (var i = 0; i < User.MaxAddresses; i++)
        {
            user.AddAddress($"A{i}", "1 Main St", null, "Berlin", "10115", "DE", false, false, Now);
        }

        // Unbounded user-controlled collections are a denial-of-service waiting for
        // someone bored.
        Assert.Throws<DomainException>(() =>
            user.AddAddress("One too many", "1 Main St", null, "Berlin", "10115", "DE", false, false, Now));
    }

    [Fact]
    public void Anonymize_redacts_personal_data_but_keeps_the_row()
    {
        var user = NewUser();
        user.AddAddress("Home", "1 Main St", null, "Berlin", "10115", "DE", true, true, Now);

        user.Anonymize(Now);

        Assert.True(user.IsAnonymized);
        Assert.Null(user.Email);
        Assert.Null(user.DisplayName);
        Assert.DoesNotContain(user.Addresses, a => a.Line1 == "1 Main St");

        // The id survives, because OrderService holds foreign keys to it and financial
        // records carry statutory retention that outranks the erasure request.
        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal(ObjectId, user.EntraObjectId);
    }

    [Fact]
    public void An_anonymized_user_is_not_resurrected_by_signing_in_again()
    {
        var user = NewUser();
        user.Anonymize(Now);

        // Without this guard, the next successful sign-in would quietly re-populate the
        // email and display name from token claims and undo the erasure.
        Assert.False(user.RefreshFromToken("ada@example.com", "Ada Lovelace", Now.AddDays(1)));
        Assert.Null(user.Email);

        Assert.Throws<DomainException>(() => user.UpdateProfile("Ada", Now));
    }
}
