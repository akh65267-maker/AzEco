using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ecommerce.ServiceDefaults;

/// <summary>
/// Every service validates the incoming Entra token itself, even though API Management
/// already did. APIM's validation protects the edge; this one protects the service from
/// anything that reaches it by another route (a misconfigured NSG, a dev tool, a
/// compromised neighbour). The cost is ~0 — JWKS is cached — and the alternative is a
/// confused-deputy vulnerability.
///
/// APIM is trusted for transport concerns (correlation id, client IP). It is never
/// trusted for identity.
/// </summary>
public static class AuthenticationExtensions
{
    public static IHostApplicationBuilder AddEcommerceAuthentication(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<AuthOptions>()
            .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations();

        var auth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

        // Keep claims exactly as Entra issued them. ASP.NET Core's legacy inbound claim
        // mapping rewrites "oid" and "scp" into long WS-Fed URIs, and then every lookup
        // in the codebase has to use a URL you cannot remember. Turn it off once, here.
        JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.Audience = auth.Audience;
                options.TokenValidationParameters = BuildValidationParameters(builder, auth);

                if (!UseLocalIssuer(builder, auth))
                {
                    // Authority drives automatic OIDC metadata + JWKS discovery, with
                    // caching and background refresh. This is what makes Entra's signing
                    // key rollover invisible to us.
                    options.Authority = $"https://login.microsoftonline.com/{auth.TenantId}/v2.0";
                    options.RequireHttpsMetadata = true;
                }

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        // 401s are cheap to cause and expensive to diagnose. Log the reason
                        // (never the token) so "works on my machine" has an answer.
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Ecommerce.Auth");
                        logger.LogWarning(context.Exception, "JWT validation failed");
                        return Task.CompletedTask;
                    },
                };
            });

        builder.Services.AddAuthorizationBuilder()
            // Baseline: authenticated, and carrying either a delegated scope (a user is
            // present) or an app role (a service is calling app-only). An authenticated
            // principal with neither is a token for some other API.
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx =>
                    ctx.User.HasClaim(c => c.Type is EcommerceClaims.Scope or EcommerceClaims.Roles))
                .Build())
            .AddPolicy(EcommercePolicies.UserContext, policy => policy
                .RequireAuthenticatedUser()
                // Requires a real user: delegated scope AND an object id to key data on.
                .RequireClaim(EcommerceClaims.Scope)
                .RequireClaim(EcommerceClaims.ObjectId));

        return builder;
    }

    private static TokenValidationParameters BuildValidationParameters(IHostApplicationBuilder builder, AuthOptions auth)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(auth.ClockSkewSeconds),
            NameClaimType = EcommerceClaims.ObjectId,
            RoleClaimType = EcommerceClaims.Roles,
        };

        if (UseLocalIssuer(builder, auth))
        {
            // Offline development only. The IsDevelopment() guard is load-bearing:
            // a symmetric signing key in configuration is a forgeable identity, and this
            // path must be impossible to reach in any deployed environment.
            parameters.ValidIssuer = auth.LocalDevIssuer;
            parameters.ValidAudiences = [auth.Audience ?? "api://ecommerce-local"];
            parameters.IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(auth.LocalDevSigningKey!));
            return parameters;
        }

        // Accept both the v2.0 issuer and the v1.0 (sts.windows.net) issuer: which one
        // you get depends on the app registration's accessTokenAcceptedVersion, and
        // getting a v1 token against a v2-only validator is a classic half-day of debugging.
        parameters.ValidIssuers =
        [
            $"https://login.microsoftonline.com/{auth.TenantId}/v2.0",
            $"https://sts.windows.net/{auth.TenantId}/",
        ];
        parameters.ValidAudiences = auth.Audience is null ? [] : [auth.Audience];
        return parameters;
    }

    private static bool UseLocalIssuer(IHostApplicationBuilder builder, AuthOptions auth) =>
        builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(auth.LocalDevSigningKey);
}

/// <summary>Claim types exactly as Entra ID emits them, with inbound mapping disabled.</summary>
public static class EcommerceClaims
{
    /// <summary>Immutable per-tenant user id. The join key to our own users table.</summary>
    public const string ObjectId = "oid";

    public const string TenantId = "tid";

    /// <summary>Delegated permissions, space-separated. Present only when a user is present.</summary>
    public const string Scope = "scp";

    /// <summary>Application roles. Present for app-only (daemon) tokens and role-assigned users.</summary>
    public const string Roles = "roles";

    public const string PreferredUsername = "preferred_username";
}

public static class EcommercePolicies
{
    /// <summary>A real signed-in user is present; endpoints may key data on their object id.</summary>
    public const string UserContext = "UserContext";
}
