using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace UserService.Tests.Integration;

/// <summary>
/// Mints access tokens shaped exactly like the ones Entra issues — same claim names
/// (<c>oid</c>, <c>tid</c>, <c>scp</c>, <c>roles</c>), same issuer and audience the
/// service is configured to accept — but signed with the local development key.
///
/// This is the reason the offline issuer exists at all. The alternative is either a
/// live Entra tenant in CI (a credential and a network dependency on every test run) or
/// bypassing authentication in tests, which means the authorization policies — the part
/// most worth testing — are never exercised.
/// </summary>
internal static class TestTokens
{
    /// <summary>A normal signed-in user: delegated scope plus an object id.</summary>
    public static string ForUser(Guid objectId, string? email = "ada@example.com", string? name = "Ada Lovelace") =>
        Create(
        [
            new Claim("oid", objectId.ToString()),
            new Claim("tid", "22222222-2222-2222-2222-222222222222"),
            new Claim("scp", "access_as_user"),
            new Claim("preferred_username", email ?? string.Empty),
            new Claim("name", name ?? string.Empty),
        ]);

    /// <summary>
    /// An app-only (daemon) token: an app ROLE and no oid, because no user is present.
    /// Anything keyed on a user must reject this rather than invent a ghost user named
    /// after a service principal.
    /// </summary>
    public static string ForService(string role = "Users.Read.All") =>
        Create(
        [
            new Claim("roles", role),
        ]);

    /// <summary>
    /// Authenticated but carrying neither a scope nor a role — a valid token issued for
    /// some other API. The default policy must reject it.
    /// </summary>
    public static string WithoutScopeOrRole() =>
        Create(
        [
            new Claim("oid", Guid.NewGuid().ToString()),
        ]);

    private static string Create(Claim[] claims)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(UserApiFactory.SigningKey));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = UserApiFactory.Issuer,
            Audience = UserApiFactory.Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = true }.CreateToken(descriptor);
    }
}
