using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecommerce.ServiceDefaults;

/// <summary>
/// Liveness and readiness are different questions and must not share an endpoint.
///
///   /alive        — is the process functioning? No dependencies checked.
///                   A failure here means RESTART ME.
///   /health/ready — can I serve traffic right now? Checks DB, Service Bus, etc.
///                   A failure here means TAKE ME OUT OF ROTATION (but do not restart).
///
/// Conflating them produces restart storms: Postgres hiccups for 20 seconds, every
/// replica fails its liveness probe, every replica restarts, cold caches and a
/// connection stampede turn a blip into an outage.
/// </summary>
public static class HealthExtensions
{
    /// <summary>Tag applied to checks that gate readiness. Untagged checks are liveness-only.</summary>
    public const string ReadyTag = "ready";

    public static IServiceCollection AddEcommerceHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return services;
    }

    public static WebApplication MapEcommerceHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Anonymous by design: the platform probe has no token. These endpoints must
        // therefore leak nothing — no dependency names, no connection strings, no
        // exception text. The default writer returns a bare status string.
        app.MapHealthChecks("/alive", new()
        {
            Predicate = registration => registration.Tags.Contains("live"),
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new()
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
        }).AllowAnonymous();

        return app;
    }
}
