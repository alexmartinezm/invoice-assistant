namespace Api.Domain;

public enum PendingActionStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
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

    public bool IsOpen(DateTimeOffset now) => Status is PendingActionStatus.Pending && now < ExpiresAt;

    /// <summary>
    /// Claims the action so it can execute. Returns false when it was already used or has lapsed —
    /// the caller must not proceed, and this is what makes approval single-use even if two tabs
    /// click at once.
    /// </summary>
    public bool TryApprove(Guid approverId, DateTimeOffset now)
    {
        if (!IsOpen(now))
        {
            Expire(now);
            return false;
        }

        Status = PendingActionStatus.Approved;
        ResolvedByUserId = approverId;
        ResolvedAt = now;
        return true;
    }

    public bool TryReject(Guid approverId, DateTimeOffset now)
    {
        if (!IsOpen(now))
        {
            Expire(now);
            return false;
        }

        Status = PendingActionStatus.Rejected;
        ResolvedByUserId = approverId;
        ResolvedAt = now;
        return true;
    }

    private void Expire(DateTimeOffset now)
    {
        if (Status is PendingActionStatus.Pending && now >= ExpiresAt)
        {
            Status = PendingActionStatus.Expired;
            ResolvedAt = now;
        }
    }
}
