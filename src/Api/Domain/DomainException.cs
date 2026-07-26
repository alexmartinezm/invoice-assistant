namespace Api.Domain;

/// <summary>
/// Raised when an operation would break an invariant of the invoicing domain.
/// The API turns it into a 409 carrying <see cref="Code"/>, and the assistant is expected
/// to report that error as-is instead of claiming the operation succeeded.
/// </summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
