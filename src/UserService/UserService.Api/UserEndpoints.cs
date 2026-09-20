using System.Security.Claims;
using Ecommerce.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UserService.Infrastructure.Persistence;

namespace UserService.Api;

public static class UserEndpoints
{
    /// <summary>
    /// App role required by other services calling app-only (no user present).
    /// A ROLE, not a scope: scopes describe what an app may do *on behalf of a user*,
    /// roles describe what an app may do *as itself*. Using a scope here would mean the
    /// daemon path only worked when a user token happened to be available.
    /// </summary>
    private const string ServiceReadRole = "Users.Read.All";

    public static WebApplication MapUserEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Everything under /me is scoped to the caller's own token. There is deliberately
        // no /users/{id} for end users: an endpoint that takes a user id is an endpoint
        // where someone will eventually forget the ownership check. If the id can only
        // come from the token, that class of bug cannot be written.
        var me = app.MapGroup("/me")
            .RequireAuthorization(EcommercePolicies.UserContext)
            .WithTags("Profile");

        me.MapGet("/", async (
            ClaimsPrincipal principal,
            CurrentUserProvider users,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetOrProvisionAsync(principal, cancellationToken).ConfigureAwait(false);
            return Results.Ok(UserResponse.From(user));
        })
        .WithName("GetMyProfile")
        .WithSummary("Returns the caller's profile, creating it on first call.");

        me.MapPut("/", async (
            UpdateProfileRequest request,
            ClaimsPrincipal principal,
            CurrentUserProvider users,
            UserDbContext db,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetOrProvisionAsync(principal, cancellationToken).ConfigureAwait(false);
            user.UpdateProfile(request.DisplayName, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(UserResponse.From(user));
        })
        .WithName("UpdateMyProfile");

        me.MapGet("/addresses", async (
            ClaimsPrincipal principal,
            CurrentUserProvider users,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetOrProvisionAsync(principal, cancellationToken).ConfigureAwait(false);
            return Results.Ok(user.Addresses.Select(AddressResponse.From).ToArray());
        })
        .WithName("ListMyAddresses");

        me.MapPost("/addresses", async (
            AddressRequest request,
            ClaimsPrincipal principal,
            CurrentUserProvider users,
            UserDbContext db,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetOrProvisionAsync(principal, cancellationToken).ConfigureAwait(false);

            var address = user.AddAddress(
                request.Label,
                request.Line1,
                request.Line2,
                request.City,
                request.PostalCode,
                request.Country,
                request.IsDefaultShipping,
                request.IsDefaultBilling,
                clock.GetUtcNow());

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Created($"/me/addresses/{address.Id}", AddressResponse.From(address));
        })
        .WithName("AddMyAddress");

        me.MapPut("/addresses/{addressId:guid}", async (
            Guid addressId,
            AddressRequest request,
            ClaimsPrincipal principal,
            CurrentUserProvider users,
            UserDbContext db,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetOrProvisionAsync(principal, cancellationToken).ConfigureAwait(false);

            // No ownership check here, and none is needed: UpdateAddress searches only
            // this user's own collection and throws if the id is not in it. The
            // authorization is structural rather than a line of code someone can forget.
            user.UpdateAddress(
                addressId,
                request.Label,
                request.Line1,
                request.Line2,
                request.City,
                request.PostalCode,
                request.Country,
                request.IsDefaultShipping,
                request.IsDefaultBilling,
                clock.GetUtcNow());

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(UserResponse.From(user));
        })
        .WithName("UpdateMyAddress");

        me.MapDelete("/addresses/{addressId:guid}", async (
            Guid addressId,
            ClaimsPrincipal principal,
            CurrentUserProvider users,
            UserDbContext db,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetOrProvisionAsync(principal, cancellationToken).ConfigureAwait(false);
            user.RemoveAddress(addressId, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("DeleteMyAddress");

        // Right to erasure. Redacts in place rather than deleting: OrderService holds
        // foreign keys here, and financial records carry statutory retention that
        // outranks the erasure request. Deleting would either cascade into records we
        // are required to keep or leave dangling references.
        me.MapDelete("/", async (
            ClaimsPrincipal principal,
            CurrentUserProvider users,
            UserDbContext db,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetOrProvisionAsync(principal, cancellationToken).ConfigureAwait(false);
            user.Anonymize(clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("AnonymizeMe")
        .WithSummary("Right to erasure: redacts personal data, keeps the row for referential integrity.");

        // ------------------------------------------------------------------
        // Service-to-service. Not exposed through APIM.
        // ------------------------------------------------------------------
        var internalApi = app.MapGroup("/internal/users")
            .RequireAuthorization(policy => policy.RequireRole(ServiceReadRole))
            .WithTags("Internal")
            .ExcludeFromDescription();

        internalApi.MapGet("/{userId:guid}", async (
            Guid userId,
            UserDbContext db,
            CancellationToken cancellationToken) =>
        {
            // AsNoTracking: this path only ever reads, and tracking would make the
            // change tracker grow for no reason on a hot service-to-service call.
            var user = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                .ConfigureAwait(false);

            return user is null
                ? Results.NotFound()
                : Results.Ok(UserSummaryResponse.From(user));
        })
        .WithName("GetUserSummary");

        return app;
    }
}

/// <summary>
/// Translates domain failures into HTTP. Kept out of the domain itself: the aggregate
/// should not know that HTTP exists, and the same rule violated from a Service Bus
/// handler needs a dead-letter, not a 400.
/// </summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails)
    : Microsoft.AspNetCore.Diagnostics.IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not Domain.DomainException domainException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        // The message is safe to return: DomainException messages are written for the
        // caller ("Country must be an ISO 3166-1 alpha-2 code"). Infrastructure
        // exceptions are NOT handled here and fall through to the generic 500, which
        // says nothing — that distinction is the whole reason the type exists.
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Title = "Request violates a business rule.",
                Detail = domainException.Message,
                Status = StatusCodes.Status400BadRequest,
            },
        }).ConfigureAwait(false);
    }
}
