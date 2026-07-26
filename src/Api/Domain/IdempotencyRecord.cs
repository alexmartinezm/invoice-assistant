namespace Api.Domain;

/// <summary>
/// A write that has already happened, kept so that repeating it returns the first answer instead of
/// doing it again.
/// </summary>
/// <remarks>
/// A tool call can be retried by the model, by the HTTP stack, or by a user clicking approve twice,
/// and "send the invoice" is not something to do twice because a socket hiccuped. The key is scoped
/// to the user who sent it: one caller cannot replay, or poison, another's writes.
/// </remarks>
public sealed class IdempotencyRecord
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Key { get; init; }

    public Guid UserId { get; init; }

    /// <summary>The operation the key was first used for. Reusing it elsewhere is a bug, not a retry.</summary>
    public required string Operation { get; init; }

    public int StatusCode { get; init; }

    public string ResponseJson { get; init; } = "null";

    public DateTimeOffset CreatedAt { get; init; }
}
