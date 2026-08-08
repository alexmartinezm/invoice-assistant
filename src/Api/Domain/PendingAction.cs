namespace Api.Domain;

/// <summary>
/// The authorization decision on a proposed write.
/// </summary>
/// <remarks>
/// <see cref="Approved"/> records that somebody said yes, and nothing more. Whether the work then
/// happened lives in <see cref="ActionExecution"/>: collapsing the two was the bug ADR 009 fixes,
/// so this enum must never grow an execution outcome.
/// </remarks>
public enum PendingActionStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
}

/// <summary>Why a proposal stopped being open, in a vocabulary an audit reader can filter on.</summary>
public enum ActionResolutionReason
{
    UserApproved,
    UserRejected,

    /// <summary>Policy refused it at approval time, which a rule or role change can produce.</summary>
    PolicyDenied,

    Expired,

    /// <summary>
    /// Closed by a deployment rather than by a person: a proposal from an older build that cannot
    /// be replayed against this one. Never executed — see the note in <c>WriteToolPlans.Replay</c>.
    /// </summary>
    DeploymentUpgrade,
}

/// <summary>
/// A write the assistant proposed and a human has not yet agreed to.
/// </summary>
/// <remarks>
/// The arguments are frozen at proposal time: whatever the model said then is what executes on
/// approval, so nothing can be changed underneath the person clicking the button. Short-lived and
/// single use, because an approval sitting around for an hour is no longer a decision about the
/// situation the user was actually looking at.
/// </remarks>
public sealed class PendingAction
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid UserId { get; init; }

    public Guid? ConversationId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>Frozen at proposal time; the approval executes exactly these arguments.</summary>
    public required string ArgsJson { get; init; }

    /// <summary>
    /// The fingerprint of tool, frozen arguments and captured revision — see
    /// <see cref="Domain.CommandHash"/>. It travels onto the execution, so the attempt can be tied
    /// back to the exact sentence a person agreed to.
    /// </summary>
    public required string CommandHash { get; init; }

    /// <summary>
    /// The target's <see cref="Invoice.Revision"/> when this was proposed, or null when the command
    /// has no existing target (<c>create_draft_invoice</c>). Approving sends it as <c>If-Match</c>:
    /// an invoice somebody edited during the approval window is a different invoice, and the write
    /// fails closed rather than landing on state the user never saw.
    /// </summary>
    public long? ExpectedResourceRevision { get; init; }

    /// <summary>
    /// What the person is agreeing to, in their own terms — "Mark invoice 2026-0041 (Acme Ibérica
    /// SL, €1,240.50) as paid". Built by the server from resolved data, never by the model.
    /// </summary>
    public required string Summary { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public PendingActionStatus Status { get; private set; } = PendingActionStatus.Pending;

    /// <summary>Who resolved it, which need not be who proposed it.</summary>
    public Guid? ResolvedByUserId { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public ActionResolutionReason? ResolutionReason { get; private set; }

    /// <summary>
    /// Still approvable: not yet resolved, and inside its window.
    /// </summary>
    /// <remarks>
    /// A read, not a claim. It answers "should a button be offered?", which is a question about
    /// rendering. Whether this caller <em>gets</em> the action is decided by the conditional update
    /// in <c>ActionResolver</c> — deliberately not by this method, because two requests can both
    /// see an open action and only one may have it. There is no <c>TryApprove</c> here for exactly
    /// that reason: an in-memory status check was the concurrency bug, not the guard against it.
    /// </remarks>
    public bool IsOpen(DateTimeOffset now) => Status is PendingActionStatus.Pending && now < ExpiresAt;
}
