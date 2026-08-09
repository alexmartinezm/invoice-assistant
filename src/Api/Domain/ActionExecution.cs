namespace Api.Domain;

/// <summary>
/// Where one attempt at an assistant write has got to.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the state that earns this enum its existence. Everything else can be
/// read off a response; <c>Unknown</c> is what a system says when it crossed a boundary it cannot
/// roll back and did not hear the answer. Rendering it as <see cref="Failed"/> would be a lie in the
/// dangerous direction — "nothing happened" about something that may well have.
/// </remarks>
public enum ActionExecutionStatus
{
    /// <summary>Authorized and recorded; the business request has not started, or is safe to resume.</summary>
    Pending,

    /// <summary>A request is in flight, or a committed delivery is waiting on a provider.</summary>
    Executing,

    /// <summary>The effect is confirmed.</summary>
    Succeeded,

    /// <summary>Something authoritative proves the effect did not happen. A new decision is needed.</summary>
    Failed,

    /// <summary>The effect may have happened and the system cannot yet prove either way.</summary>
    Unknown,
}

/// <summary>How the write was authorized: by a policy rule, or by a person.</summary>
public enum ExecutionDecision
{
    Auto,
    Confirmed,
}

/// <summary>
/// The durable record of an attempt to perform an assistant write, and of what became of it.
/// </summary>
/// <remarks>
/// <para>
/// It exists because <see cref="PendingActionStatus.Approved"/> was being read as "the work
/// happened", and it is not: it is a person's decision, recorded before anything was tried. The two
/// facts diverge exactly when it matters — a crash, a timeout, an API refusal after a human said yes
/// — so they are two rows, linked, never one status.
/// </para>
/// <para>
/// <see cref="Id"/> doubles as the idempotency key for every attempt this execution makes, which is
/// what makes a retry a retry rather than a second write. It is stable for the life of the
/// execution; a new key would mean a new execution, and that is a decision, not a retry.
/// </para>
/// </remarks>
public sealed class ActionExecution
{
    /// <summary>How much of a failure detail is kept. Enough to diagnose, not enough to be a copy of the body.</summary>
    public const int MaxErrorDetail = 500;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The approval this executes, or null for a write policy allowed on its own.</summary>
    public Guid? PendingActionId { get; init; }

    /// <summary>Whose identity the write ran under — the approver, not necessarily the proposer.</summary>
    public Guid UserId { get; init; }

    public Guid? ConversationId { get; init; }

    public required string ToolName { get; init; }

    public ExecutionDecision Decision { get; init; }

    /// <summary>Binds the attempt to the exact command that was authorized. See <see cref="Domain.CommandHash"/>.</summary>
    public required string CommandHash { get; init; }

    public ActionExecutionStatus Status { get; private set; } = ActionExecutionStatus.Pending;

    public int AttemptCount { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When a reconciler should look at this again. Null while nothing is owed.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>
    /// When the in-flight attempt stops being anybody's.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="ActionExecutionStatus.Executing"/> was a trap: the claim moved the row
    /// there and nothing could ever move it back, so a process that died mid-request left the
    /// execution — and the card watching it — stuck on "executing" for ever. A lease makes the state
    /// reclaimable. Reclaiming is safe precisely because the retry carries the same
    /// <see cref="IdempotencyKey"/>: the receipt decides whether it is a resume or a replay.
    /// </remarks>
    public DateTimeOffset? AttemptExpiresAt { get; private set; }

    public int? ResultStatusCode { get; private set; }

    /// <summary>The settled response, stored so a resumed approval replays it instead of re-running.</summary>
    public string? ResultJson { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorDetail { get; private set; }

    /// <summary>The external delivery this execution is waiting on, when there is one.</summary>
    public Guid? DeliveryId { get; private set; }

    public string? ProviderMessageId { get; private set; }

    /// <summary>
    /// The <c>Idempotency-Key</c> every attempt of this execution sends. Stable by construction: it
    /// is the execution's own identity, so a retry cannot accidentally mint a fresh one.
    /// </summary>
    public string IdempotencyKey => Id.ToString();

    /// <summary>True once the outcome is known and will not change again.</summary>
    public bool IsSettled => Status is ActionExecutionStatus.Succeeded or ActionExecutionStatus.Failed;

    /// <remarks>
    /// There is no <c>Start</c> here, and the omission is the point. Claiming an attempt is a
    /// decision between concurrent requests, so it is a conditional <c>UPDATE</c> in
    /// <c>ActionExecutor</c>, not a method two callers could both reach. The settle methods below
    /// are safe as entity transitions because only the caller holding that claim reaches them —
    /// and if one somehow does not, the terminal-state guard refuses rather than rewriting history.
    /// </remarks>
    public void Succeed(int statusCode, string? resultJson, DateTimeOffset now)
    {
        RequireOpen();

        Status = ActionExecutionStatus.Succeeded;
        ResultStatusCode = statusCode;
        ResultJson = resultJson;
        ErrorCode = null;
        ErrorDetail = null;
        NextAttemptAt = null;
        AttemptExpiresAt = null;
        CompletedAt = now;
    }

    /// <summary>
    /// A refusal or an authoritative failure: the intended effect provably did not happen. Only
    /// reached from evidence — a deterministic 4xx, a rejected delivery — never from a timeout.
    /// </summary>
    public void Fail(int? statusCode, string errorCode, string? detail, DateTimeOffset now)
    {
        RequireOpen();

        Status = ActionExecutionStatus.Failed;
        ResultStatusCode = statusCode;
        ErrorCode = errorCode;
        ErrorDetail = Truncate(detail);
        NextAttemptAt = null;
        AttemptExpiresAt = null;
        CompletedAt = now;
    }

    /// <summary>
    /// The request crossed a non-transactional boundary and the answer was lost. Schedules the
    /// reconciler rather than retrying: repeating a write nobody can prove did not happen is the
    /// one move this whole entity exists to prevent.
    /// </summary>
    public void MarkUnknown(string errorCode, string? detail, DateTimeOffset reconcileAt)
    {
        RequireOpen();

        Status = ActionExecutionStatus.Unknown;
        ErrorCode = errorCode;
        ErrorDetail = Truncate(detail);
        NextAttemptAt = reconcileAt;
        AttemptExpiresAt = null;
    }

    /// <summary>
    /// The local write committed and handed off to an outbox. The execution stays
    /// <see cref="ActionExecutionStatus.Executing"/> until the delivery settles, so an invoice
    /// reading <c>Sent</c> never doubles as a claim that the customer received it.
    /// </summary>
    public void AwaitDelivery(Guid deliveryId, int statusCode, string? resultJson, DateTimeOffset now)
    {
        RequireOpen();

        Status = ActionExecutionStatus.Executing;
        DeliveryId = deliveryId;
        ResultStatusCode = statusCode;
        ResultJson = resultJson;
        LastAttemptAt = now;

        // The HTTP attempt is over; what the execution waits on now is the outbox. Clearing the
        // lease stops a reclaim treating a perfectly healthy queued delivery as an abandoned request.
        AttemptExpiresAt = null;
    }

    public void RecordProviderMessage(string providerMessageId) => ProviderMessageId = providerMessageId;

    /// <summary>
    /// Succeeded and Failed are terminal. Everything else may still move, including Unknown — an
    /// execution reconciled from an authoritative receipt is the whole reason that state exists.
    /// </summary>
    private void RequireOpen()
    {
        if (IsSettled)
        {
            throw new InvalidOperationException($"An execution that already {Status} cannot be settled again.");
        }
    }

    private static string? Truncate(string? detail) => detail is null || detail.Length <= MaxErrorDetail
        ? detail
        : detail[..MaxErrorDetail];
}
