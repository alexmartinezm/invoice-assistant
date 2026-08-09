namespace Api.Domain;

/// <summary>
/// A write that has already happened, kept so that repeating it returns the first answer instead of
/// doing it again.
/// </summary>
/// <remarks>
/// <para>
/// The row is claimed and completed inside the same transaction as the business change, so a
/// committed receipt is proof the effect happened and the absence of one is proof it did not. That
/// is what makes it authoritative enough to reconcile an execution whose answer was lost. The
/// previous design wrote the receipt in a second save after the handler had already committed,
/// which left a window where the effect existed and the receipt did not — and the next retry would
/// perform it again.
/// </para>
/// <para>
/// The key is scoped to the user who sent it: one caller cannot replay, or poison, another's writes.
/// It is also bound to <see cref="RequestHash"/>, so the same key with different content is refused
/// rather than answered with a plausible-looking reply to a question nobody asked.
/// </para>
/// </remarks>
public sealed class IdempotencyRecord
{
    /// <summary>
    /// How long a receipt is kept, and — because expiry is now enforced by deleting rows rather
    /// than by filtering reads — how long the unique key is reserved. Those two were separate
    /// numbers before, which meant a key could be simultaneously "too old to replay" and "already
    /// taken".
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Key { get; init; }

    public Guid UserId { get; init; }

    /// <summary>The operation the key was first used for. Reusing it elsewhere is a bug, not a retry.</summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Fingerprint of method, path, body and precondition. Null only for rows written by a build
    /// that predates it, which stay replay-only under the operation check until they expire.
    /// </summary>
    public string? RequestHash { get; init; }

    public int StatusCode { get; private set; }

    public string? ResponseJson { get; private set; }

    /// <summary>
    /// The allowlisted response headers a replay restores — <c>Location</c>, <c>ETag</c> — so the
    /// second call to see a created resource is told the same things the first one was. Null when
    /// the response carried none of them, not merely when nobody thought to record them.
    /// </summary>
    public string? ResponseHeadersJson { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the receipt was settled. Null means a claim without an outcome — which, because claim
    /// and completion share a transaction, is a state no committed row can be in.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The execution this receipt belongs to, when the caller was one.</summary>
    public Guid? ActionExecutionId { get; init; }

    public bool IsCompleted => CompletedAt is not null;

    public void Complete(int statusCode, string? responseJson, DateTimeOffset now)
    {
        StatusCode = statusCode;
        ResponseJson = responseJson;
        CompletedAt = now;
    }
}
