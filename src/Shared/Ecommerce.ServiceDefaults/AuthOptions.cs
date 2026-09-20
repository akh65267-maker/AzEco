using System.ComponentModel.DataAnnotations;

namespace Ecommerce.ServiceDefaults;

/// <summary>
/// Bound from configuration section "Auth". None of these are secrets — tenant ids,
/// client ids and audiences are public values. They belong in environment variables /
/// App Settings, never in Key Vault (a Key Vault full of non-secrets is a startup
/// dependency you did not need).
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Entra tenant GUID (or "common" for multi-tenant, which we do not use).</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The audience this API accepts. Either the App ID URI (api://ecom-api) or the
    /// client id GUID — it must match the <c>aud</c> claim Entra actually issues,
    /// which depends on how the client requested the token. Mismatch here is the
    /// single most common cause of "valid token, 401 anyway".
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Local-development escape hatch. When set, tokens are validated against this
    /// symmetric key instead of Entra's JWKS, so the stack runs with no internet.
    /// Guarded so it can never activate outside Development — see AuthenticationExtensions.
    /// </summary>
    public string? LocalDevSigningKey { get; set; }

    /// <summary>Issuer expected for locally-minted development tokens.</summary>
    public string LocalDevIssuer { get; set; } = "https://localhost/ecommerce-dev";

    [Range(0, 600)]
    public int ClockSkewSeconds { get; set; } = 60;
}
