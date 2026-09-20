namespace Ecommerce.Contracts;

/// <summary>
/// The envelope every published event shares.
///
/// This is the ONLY type shared between services, and it is shared deliberately:
/// an event contract is a public API. Sharing DTOs, entities or "Common" helper
/// libraries across service boundaries is how a microservice system quietly
/// becomes a distributed monolith — a change in one service then forces a
/// coordinated redeploy of all of them, which is the one thing this architecture
/// exists to avoid.
///
/// Versioning rules (see docs/architecture.md):
///   1. Additive changes only. Never rename or remove a field.
///   2. A breaking change is a NEW type name: OrderPlaced -> OrderPlacedV2.
///   3. Publish both versions during migration, retire the old one afterwards.
///   4. Consumers ignore unknown fields and dead-letter unknown versions
///      rather than guessing.
/// </summary>
public abstract record IntegrationEvent
{
    /// <summary>
    /// Stable identity of this event. It is the outbox row id, so it does NOT change
    /// when the dispatcher retries — which is precisely what makes consumer-side
    /// deduplication work. Service Bus guarantees at-least-once delivery; this id
    /// is how a consumer turns that into effectively-once processing.
    /// </summary>
    public required Guid EventId { get; init; }

    /// <summary>When the fact occurred, per the producing service. Always UTC.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Discriminator used for Service Bus subscription filters and for routing on the
    /// consumer side. Mirrored into the message's ApplicationProperties so the broker
    /// can filter without deserializing the body.
    /// </summary>
    public abstract string EventType { get; }

    /// <summary>Incremented only on breaking changes; see rule 2 above.</summary>
    public virtual int EventVersion => 1;
}
