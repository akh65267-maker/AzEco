namespace OrderService.Infrastructure;

/// <summary>
/// Anchor type for assembly scanning (DI registration, EF migrations, architecture tests).
/// Using a dedicated marker instead of a real type means a refactor cannot silently
/// break the scan.
/// </summary>
public sealed class AssemblyMarker;
