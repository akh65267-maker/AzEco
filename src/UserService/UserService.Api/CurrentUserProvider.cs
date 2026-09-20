using System.Diagnostics;
using System.Security.Claims;
using Ecommerce.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UserService.Domain;
using UserService.Infrastructure.Persistence;

namespace UserService.Api;

/// <summary>
/// Resolves the authenticated Entra principal to a local <see cref="User"/> row,
/// creating it on first sight (just-in-time provisioning).
///
/// WHY THERE IS NO REGISTRATION ENDPOINT
/// Entra already performed the signup. A /register endpoint would be a second source of
/// truth about who exists, and the two would disagree the first time someone was invited
/// through the Entra portal instead of the app.
///
/// WHY THE INSERT CAN LOSE A RACE
/// A user's client commonly fires several requests the moment it gets a token — profile,
/// addresses, cart. All of them arrive with no local row, all of them check, all of them
/// find nothing, all of them insert. Check-then-insert is a race by construction; the
/// only reliable arbiter is the unique index on entra_object_id, so the code below
/// treats a unique violation as a normal outcome rather than an error. Without this,
/// signing in produces a sporadic 500 that is almost impossible to reproduce by hand.
/// </summary>
public sealed class CurrentUserProvider(
    UserDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<CurrentUserProvider> logger)
{
    private const string UniqueViolation = "23505";

    public async Task<User> GetOrProvisionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var (objectId, tenantId) = ReadIdentity(principal);
        var email = principal.FindFirstValue(EcommerceClaims.PreferredUsername)
                    ?? principal.FindFirstValue(ClaimTypes.Email);
        var displayName = principal.FindFirstValue("name");

        Activity.Current?.SetTag(EcommerceTelemetry.Attributes.UserId, objectId);

        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.EntraObjectId == objectId, cancellationToken)
            .ConfigureAwait(false);

        if (user is not null)
        {
            // Only writes when a claim actually changed — otherwise every read becomes a
            // write and the resulting UPDATE storm looks like mysterious database load
            // long before anyone suspects the profile lookup.
            if (user.RefreshFromToken(email, displayName, timeProvider.GetUtcNow()))
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return user;
        }

        return await ProvisionAsync(objectId, tenantId, email, displayName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<User> ProvisionAsync(
        Guid objectId,
        Guid tenantId,
        string? email,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var user = User.ProvisionFromToken(objectId, tenantId, email, displayName, timeProvider.GetUtcNow());
        dbContext.Users.Add(user);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Log.ProvisionedUser(logger, objectId);
            return user;
        }
        catch (DbUpdateException ex) when ((ex.InnerException as PostgresException)?.SqlState == UniqueViolation)
        {
            // Another concurrent request won. That is a success, not a failure: the row
            // we wanted now exists. Detach our losing copy so the context is not left
            // holding an entity the database rejected, then read the winner.
            dbContext.Entry(user).State = EntityState.Detached;

            Log.ConcurrentProvisioning(logger, objectId);

            return await dbContext.Users
                .FirstAsync(u => u.EntraObjectId == objectId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads identity from claims only. Never from a header, a query parameter, or
    /// anything APIM attached — APIM is trusted for transport, never for identity.
    /// </summary>
    private static (Guid ObjectId, Guid TenantId) ReadIdentity(ClaimsPrincipal principal)
    {
        var oid = principal.FindFirstValue(EcommerceClaims.ObjectId);
        var tid = principal.FindFirstValue(EcommerceClaims.TenantId);

        if (!Guid.TryParse(oid, out var objectId))
        {
            // A validated token with no oid is an app-only (daemon) token: there is no
            // user behind it. Failing loudly here beats provisioning a ghost user named
            // after a service principal.
            throw new InvalidOperationException(
                "Token contains no 'oid' claim. This endpoint requires a user context, not an app-only token.");
        }

        _ = Guid.TryParse(tid, out var tenantId);
        return (objectId, tenantId);
    }
}

/// <summary>
/// Source-generated logging. The compiler emits a cached delegate per message, so the
/// arguments are never boxed and the message template is never formatted when the level
/// is disabled. On a path that runs for every authenticated request, the interpolated
/// alternative allocates on every call to produce a string that is usually discarded —
/// which is why CA1873 fails the build rather than warning.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Provisioned local user for Entra object id {EntraObjectId}")]
    public static partial void ProvisionedUser(ILogger logger, Guid entraObjectId);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "Concurrent provisioning for {EntraObjectId}; using the row the other request won")]
    public static partial void ConcurrentProvisioning(ILogger logger, Guid entraObjectId);
}
