namespace UserService.Domain;

/// <summary>
/// A business rule was violated. Distinct from an infrastructure failure, because the
/// two need opposite handling: a domain violation is the caller's fault (400/409, do not
/// retry), an infrastructure failure is ours (500, retry may help).
///
/// Collapsing both into a generic exception is how a validation mistake ends up being
/// retried five times and then dead-lettered.
/// </summary>
public sealed class DomainException(string message) : Exception(message);
