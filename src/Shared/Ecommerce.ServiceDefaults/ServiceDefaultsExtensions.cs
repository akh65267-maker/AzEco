using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecommerce.ServiceDefaults;

/// <summary>
/// Two calls a service must make. Everything cross-cutting is here so that adding a
/// fourth service (CartService) inherits observability, auth, health and resilience
/// for free rather than by copy-paste — copy-paste is how one service ends up
/// invisible in the App Map for six months.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEcommerceObservability();
        builder.AddEcommerceAuthentication();
        builder.Services.AddEcommerceHealthChecks();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Standard resilience = retry + circuit breaker + timeout + rate limiter.
            // Retries are safe here ONLY because it excludes non-idempotent methods
            // by default. Any POST that must be retried needs an Idempotency-Key.
            http.AddStandardResilienceHandler();
        });

        // RFC 7807 problem details for every unhandled failure, so clients get a
        // consistent error shape instead of an HTML error page.
        builder.Services.AddProblemDetails();

        return builder;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();
        app.UseEcommerceCorrelation();

        // HSTS + HTTPS redirection belong at the edge (APIM) in Azure, but keeping
        // them here means a service is still safe if it is ever exposed directly.
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapEcommerceHealthChecks();

        return app;
    }
}
