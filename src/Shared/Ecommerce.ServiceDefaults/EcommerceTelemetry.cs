using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Ecommerce.ServiceDefaults;

/// <summary>
/// The one place that owns instrumentation names.
///
/// Why a shared static: an <see cref="ActivitySource"/> only produces spans if its name
/// was registered with the tracer provider. Scattering <c>new ActivitySource("...")</c>
/// across projects guarantees that sooner or later someone creates a source nobody listens
/// to, and their spans silently vanish. One name, registered once, in one file.
/// </summary>
public static class EcommerceTelemetry
{
    /// <summary>Prefix for every activity source and meter in the system.</summary>
    public const string Namespace = "Ecommerce";

    /// <summary>
    /// Manual spans for work that no auto-instrumentation covers — outbox dispatch,
    /// Service Bus handlers, SAS minting, domain operations worth seeing in a trace.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(Namespace);

    /// <summary>
    /// Business metrics. Unsampled and cheap; prefer these over counting log lines.
    /// </summary>
    public static readonly Meter Meter = new(Namespace);

    /// <summary>
    /// Well-known attribute keys. Constants because a typo in an attribute name is
    /// invisible at compile time and produces a second, near-identical dimension
    /// in Application Insights that nobody notices for a month.
    /// </summary>
    public static class Attributes
    {
        public const string UserId = "ecommerce.user_id";
        public const string OrderId = "ecommerce.order_id";
        public const string ProductId = "ecommerce.product_id";
        public const string EventType = "messaging.ecommerce.event_type";
        public const string EventVersion = "messaging.ecommerce.event_version";
        public const string MessageId = "messaging.message.id";
    }
}
