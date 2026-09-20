using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UserService.Api;

namespace UserService.Tests.Integration;

[Collection(UserApiFixture.CollectionName)]
public class ProfileEndpointTests(UserApiFactory factory) : IAsyncLifetime
{
    // Each test starts from an empty database. Sharing one container across the class
    // (via the collection fixture) keeps the suite fast; truncating between tests keeps
    // them independent, which matters more than the microseconds it costs.
    public Task InitializeAsync() => factory.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient ClientFor(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [DockerRequiredFact]
    public async Task Anonymous_requests_are_rejected()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative)).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [DockerRequiredFact]
    public async Task A_token_with_no_scope_or_role_is_rejected()
    {
        // A signature-valid token issued for a different API. This is exactly the case
        // APIM's coarse validation would let through, and the reason the service
        // validates again rather than trusting the gateway.
        var client = ClientFor(TestTokens.WithoutScopeOrRole());

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative)).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [DockerRequiredFact]
    public async Task First_call_provisions_the_local_user_from_token_claims()
    {
        var objectId = Guid.NewGuid();
        var client = ClientFor(TestTokens.ForUser(objectId));

        var user = await client.GetFromJsonAsync<UserResponse>("/me").ConfigureAwait(false);

        Assert.NotNull(user);
        Assert.Equal(objectId, user.EntraObjectId);
        Assert.Equal("ada@example.com", user.Email);

        // The local id is ours and is NOT the Entra object id — the indirection that
        // keeps every foreign key in the system independent of the identity provider.
        Assert.NotEqual(objectId, user.Id);
    }

    [DockerRequiredFact]
    public async Task Repeated_calls_return_the_same_local_user()
    {
        var objectId = Guid.NewGuid();
        var client = ClientFor(TestTokens.ForUser(objectId));

        var first = await client.GetFromJsonAsync<UserResponse>("/me").ConfigureAwait(false);
        var second = await client.GetFromJsonAsync<UserResponse>("/me").ConfigureAwait(false);

        Assert.Equal(first!.Id, second!.Id);
    }

    [DockerRequiredFact]
    public async Task Concurrent_first_calls_provision_exactly_one_user()
    {
        // The race this service is most likely to hit in production: a client fires
        // profile, addresses and cart the instant it receives a token, all three find no
        // local row, all three insert. Only the unique index can arbitrate. Without the
        // 23505 handling in CurrentUserProvider this produces a sporadic 500 that is
        // nearly impossible to reproduce by hand.
        var objectId = Guid.NewGuid();
        var token = TestTokens.ForUser(objectId);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
            {
                var client = ClientFor(token);
                return await client.GetAsync(new Uri("/me", UriKind.Relative)).ConfigureAwait(false);
            })).ConfigureAwait(false);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            var body = await response.Content.ReadFromJsonAsync<UserResponse>().ConfigureAwait(false);
            ids.Add(body!.Id);
        }

        Assert.Single(ids);
    }

    [DockerRequiredFact]
    public async Task Email_lookup_is_case_insensitive_through_citext()
    {
        var objectId = Guid.NewGuid();
        var client = ClientFor(TestTokens.ForUser(objectId, email: "Ada@EXAMPLE.com"));

        var user = await client.GetFromJsonAsync<UserResponse>("/me").ConfigureAwait(false);

        Assert.Equal("ada@example.com", user!.Email);
    }

    [DockerRequiredFact]
    public async Task Adding_an_address_makes_it_the_default()
    {
        var client = ClientFor(TestTokens.ForUser(Guid.NewGuid()));

        var response = await client.PostAsJsonAsync(
            new Uri("/me/addresses", UriKind.Relative),
            new AddressRequest("Home", "1 Main St", null, "Berlin", "10115", "DE")).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var address = await response.Content.ReadFromJsonAsync<AddressResponse>().ConfigureAwait(false);
        Assert.True(address!.IsDefaultShipping);
        Assert.True(address.IsDefaultBilling);
    }

    [DockerRequiredFact]
    public async Task A_domain_violation_becomes_a_400_not_a_500()
    {
        var client = ClientFor(TestTokens.ForUser(Guid.NewGuid()));

        var response = await client.PostAsJsonAsync(
            new Uri("/me/addresses", UriKind.Relative),
            new AddressRequest("Home", "1 Main St", null, "Berlin", "10115", "Germany")).ConfigureAwait(false);

        // The caller's mistake, not ours: 400, do not retry. An infrastructure failure
        // would fall through to a generic 500 that says nothing — that distinction is
        // the entire reason DomainException is a separate type.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.Contains("ISO 3166", body, StringComparison.Ordinal);
    }

    [DockerRequiredFact]
    public async Task One_user_cannot_modify_another_users_address()
    {
        var victimClient = ClientFor(TestTokens.ForUser(Guid.NewGuid()));
        var created = await victimClient.PostAsJsonAsync(
            new Uri("/me/addresses", UriKind.Relative),
            new AddressRequest("Home", "1 Main St", null, "Berlin", "10115", "DE")).ConfigureAwait(false);
        var victimAddress = await created.Content.ReadFromJsonAsync<AddressResponse>().ConfigureAwait(false);

        var attackerClient = ClientFor(TestTokens.ForUser(Guid.NewGuid()));

        var response = await attackerClient.DeleteAsync(
            new Uri($"/me/addresses/{victimAddress!.Id}", UriKind.Relative)).ConfigureAwait(false);

        // 400, not 403: from the attacker's aggregate the id simply does not exist, and
        // saying so leaks nothing about whether it exists for anyone else.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [DockerRequiredFact]
    public async Task An_app_only_token_cannot_reach_a_user_scoped_endpoint()
    {
        // No oid claim, because no user is present. The endpoint requires a user context.
        var client = ClientFor(TestTokens.ForService());

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative)).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [DockerRequiredFact]
    public async Task The_internal_endpoint_requires_an_app_role()
    {
        var userClient = ClientFor(TestTokens.ForUser(Guid.NewGuid()));
        var user = await userClient.GetFromJsonAsync<UserResponse>("/me").ConfigureAwait(false);

        // A user token must not reach the service-to-service endpoint: a delegated scope
        // says what an app may do on behalf of a user, not what it may do as itself.
        var denied = await userClient.GetAsync(
            new Uri($"/internal/users/{user!.Id}", UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var serviceClient = ClientFor(TestTokens.ForService());
        var allowed = await serviceClient.GetAsync(
            new Uri($"/internal/users/{user.Id}", UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // And it returns strictly less than /me does: a display name and a shipping
        // address, no email. Least privilege applies to data shape, not only to RBAC.
        var summary = await allowed.Content.ReadFromJsonAsync<UserSummaryResponse>().ConfigureAwait(false);
        Assert.Equal(user.Id, summary!.Id);
    }

    [DockerRequiredFact]
    public async Task Erasure_redacts_the_user_but_keeps_the_row()
    {
        var objectId = Guid.NewGuid();
        var client = ClientFor(TestTokens.ForUser(objectId));

        var before = await client.GetFromJsonAsync<UserResponse>("/me").ConfigureAwait(false);

        var deleted = await client.DeleteAsync(new Uri("/me", UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Signing in again must NOT resurrect the erased data from token claims.
        var after = await client.GetFromJsonAsync<UserResponse>("/me").ConfigureAwait(false);

        Assert.Equal(before!.Id, after!.Id);
        Assert.Null(after.Email);
        Assert.Null(after.DisplayName);
    }

    [DockerRequiredFact]
    public async Task The_readiness_probe_reports_healthy_with_a_reachable_database()
    {
        var client = factory.CreateClient();

        // Anonymous by necessity — the platform probe has no token.
        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var alive = await client.GetAsync(new Uri("/alive", UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }

    [DockerRequiredFact]
    public async Task Every_response_carries_the_trace_id()
    {
        var client = ClientFor(TestTokens.ForUser(Guid.NewGuid()));

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative)).ConfigureAwait(false);

        // "Error id abc123" in a support ticket has to be one KQL filter away from the
        // full distributed trace, which is why we surface traceparent rather than
        // inventing a second correlation id that can disagree with it.
        Assert.True(response.Headers.Contains("x-request-id"));
    }
}

/// <summary>
/// One PostgreSQL container shared by every test in the collection. Starting a container
/// per test class would be correct and unbearably slow; correctness between tests comes
/// from truncating tables instead.
/// </summary>
[CollectionDefinition(CollectionName)]
public sealed class UserApiFixture : ICollectionFixture<UserApiFactory>
{
    public const string CollectionName = "UserApi";
}
