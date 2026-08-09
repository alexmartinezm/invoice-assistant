using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Assistant;
using Api.Domain;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Actions;

/// <summary>Why a claim on a proposal did not go through.</summary>
public enum ClaimFailure
{
    /// <summary>Somebody else resolved it first, or it lapsed. The recorded outcome stands.</summary>
    AlreadyResolved,

    /// <summary>
    /// The row predates this build's fingerprinting, so there is nothing to bind an approval to.
    /// Refused and closed rather than executed on trust.
    /// </summary>
    NotFingerprinted,

    /// <summary>
    /// The stored fingerprint does not describe the stored command. Something changed the row
    /// between proposal and approval, so the sentence a person agreed to is no longer the sentence
    /// that would run.
    /// </summary>
    Tampered,
}

/// <summary>
/// The outcome of trying to become the one caller who resolves a proposal.
/// </summary>
/// <param name="Action">The row as the database now has it — the winner's version, if there is one.</param>
/// <param name="Execution">
/// The execution bound to this decision. Present on a win, and also on a loss when the same caller
/// already won earlier: that is what makes a retried approval resume rather than start again.
/// </param>
public sealed record ActionClaim(
    bool Won,
    PendingAction Action,
    ActionExecution? Execution,
    ClaimFailure? Failure);

/// <summary>
/// The only place a <see cref="PendingAction"/> changes state.
/// </summary>
/// <remarks>
/// <para>
/// Every resolution is a conditional <c>UPDATE</c> whose affected-row count <em>is</em> the
/// decision. The previous implementation loaded the entity, checked <c>Status</c> in memory and
/// saved: two requests could both read <c>Pending</c> before either save became visible, so
/// single-use was a property of timing rather than a constraint. Twenty tabs clicking approve now
/// produce one row change and nineteen losers, whatever the interleaving.
/// </para>
/// <para>
/// The claim, the authorization audit and the execution insert are one transaction. Half of that
/// committed is worse than none of it: an approved action with no execution is a decision nobody can
/// resume, and an execution with no decision is a write nobody authorized.
/// </para>
/// </remarks>
public sealed class ActionResolver(AppDbContext db, IClock clock, ILogger<ActionResolver> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Claims a proposal for execution under <paramref name="approverId"/>, and — in the same
    /// transaction — records the authorization and mints the execution identity the write will run
    /// under. Nothing is sent to the business API until this has committed.
    /// </summary>
    public async Task<ActionClaim> ClaimForApprovalAsync(
        PendingAction action,
        JsonElement args,
        Guid approverId,
        string reason,
        CancellationToken cancellationToken)
    {
        using var activity = AssistantTelemetry.Source.StartActivity("assistant.action.resolve");
        activity?.SetTag("assistant.action_id", action.Id);
        activity?.SetTag("assistant.tool", action.ToolName);
        activity?.SetTag("assistant.decision", "approve");

        // A proposal from a build that did not fingerprint commands cannot be bound to anything, so
        // approving it would be executing on trust. Closed with a reason instead — see the note in
        // the DurableActionLedger migration.
        if (string.IsNullOrWhiteSpace(action.CommandHash))
        {
            logger.LogWarning("Pending action {ActionId} predates command fingerprinting; refusing it.", action.Id);

            await CompareAndSetAsync(
                action.Id, PendingActionStatus.Expired, ActionResolutionReason.DeploymentUpgrade,
                resolvedBy: null, requireOpen: false, cancellationToken);

            activity?.SetTag("assistant.claim", "not_fingerprinted");
            return new ActionClaim(
                Won: false,
                await ReloadAsync(action.Id, cancellationToken) ?? action,
                Execution: null,
                ClaimFailure.NotFingerprinted);
        }

        // The fingerprint is only worth carrying if somebody checks it. Recomputing it here is what
        // turns it from a value copied onto the execution into a precondition of approving at all:
        // an ArgsJson or ExpectedResourceRevision edited after the proposal was shown no longer
        // matches, and the approval buys nothing. Constant-time because the comparison is the guard.
        if (!Matches(CommandHash.Of(action.ToolName, action.ArgsJson, action.ExpectedResourceRevision), action.CommandHash))
        {
            logger.LogError(
                "Pending action {ActionId} does not match its command fingerprint; refusing it.", action.Id);

            await CompareAndSetAsync(
                action.Id, PendingActionStatus.Rejected, ActionResolutionReason.PolicyDenied,
                resolvedBy: null, requireOpen: false, cancellationToken);

            activity?.SetTag("assistant.claim", "tampered");
            return new ActionClaim(
                Won: false,
                await ReloadAsync(action.Id, cancellationToken) ?? action,
                Execution: null,
                ClaimFailure.Tampered);
        }

        var now = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await CompareAndSetAsync(
            action.Id, PendingActionStatus.Approved, ActionResolutionReason.UserApproved,
            approverId, requireOpen: true, cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);

            var current = await ReloadAsync(action.Id, cancellationToken) ?? action;
            activity?.SetTag("assistant.claim", "lost");

            return new ActionClaim(
                Won: false,
                current,
                await ExecutionForAsync(action.Id, cancellationToken),
                ClaimFailure.AlreadyResolved);
        }

        var execution = new ActionExecution
        {
            PendingActionId = action.Id,
            UserId = approverId,
            ConversationId = action.ConversationId,
            ToolName = action.ToolName,
            Decision = ExecutionDecision.Confirmed,
            CommandHash = action.CommandHash,
            CreatedAt = now,
        };

        db.ActionExecutions.Add(execution);
        db.AuditEvents.Add(Audit(
            approverId, action.ToolName, args, AuditDecision.Confirmed, reason, action.ConversationId, execution.Id, now));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetTag("assistant.claim", "won");
        activity?.SetTag("assistant.execution_id", execution.Id);
        activity?.SetTag("assistant.actor_id", approverId);

        AssistantTelemetry.ExecutionsStarted.Add(
            1,
            new KeyValuePair<string, object?>("tool", action.ToolName),
            new KeyValuePair<string, object?>("decision", nameof(ExecutionDecision.Confirmed)));

        return new ActionClaim(Won: true, await ReloadAsync(action.Id, cancellationToken) ?? action, execution, Failure: null);
    }

    /// <summary>
    /// Closes a proposal without executing it, and records why in the same transaction. Used for a
    /// rejection and for a policy refusal at approval time, which are different reasons for the same
    /// absence of a write.
    /// </summary>
    /// <remarks>
    /// The audit row commits with the resolution for the same reason the approval path's does: a
    /// closed proposal whose reason never landed is a decision nobody can account for, and the
    /// rejection path used to write no audit row at all — the one decision a person makes explicitly
    /// was the one the ledger did not record.
    /// </remarks>
    public async Task<ActionClaim> ResolveWithoutExecutingAsync(
        PendingAction action,
        Guid resolvedBy,
        PendingActionStatus status,
        ActionResolutionReason reason,
        JsonElement args,
        AuditDecision auditDecision,
        string auditReason,
        CancellationToken cancellationToken)
    {
        using var activity = AssistantTelemetry.Source.StartActivity("assistant.action.resolve");
        activity?.SetTag("assistant.action_id", action.Id);
        activity?.SetTag("assistant.tool", action.ToolName);
        activity?.SetTag("assistant.decision", status.ToString().ToLowerInvariant());

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await CompareAndSetAsync(
            action.Id, status, reason, resolvedBy, requireOpen: true, cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            activity?.SetTag("assistant.claim", "lost");

            return new ActionClaim(
                Won: false,
                await ReloadAsync(action.Id, cancellationToken) ?? action,
                await ExecutionForAsync(action.Id, cancellationToken),
                ClaimFailure.AlreadyResolved);
        }

        db.AuditEvents.Add(Audit(
            resolvedBy, action.ToolName, args, auditDecision, auditReason,
            action.ConversationId, executionId: null, clock.UtcNow));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetTag("assistant.claim", "won");
        activity?.SetTag("assistant.actor_id", resolvedBy);

        return new ActionClaim(
            Won: true, await ReloadAsync(action.Id, cancellationToken) ?? action, Execution: null, Failure: null);
    }

    /// <summary>
    /// Records that a proposal has lapsed, so a queue that has stopped offering it says why. Nothing
    /// depends on this having run: <see cref="PendingAction.IsOpen"/> is evaluated against the clock
    /// every time, and the compare-and-set carries the same deadline.
    /// </summary>
    public Task MarkExpiredAsync(Guid actionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        return db.PendingActions
            .Where(a => a.Id == actionId && a.Status == PendingActionStatus.Pending && a.ExpiresAt <= now)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(a => a.Status, PendingActionStatus.Expired)
                    .SetProperty(a => a.ResolutionReason, (ActionResolutionReason?)ActionResolutionReason.Expired)
                    .SetProperty(a => a.ResolvedAt, (DateTimeOffset?)now),
                cancellationToken);
    }

    /// <summary>Records a decision that never produced an execution — a denial, or a block.</summary>
    public async Task AuditAsync(
        Guid userId,
        string toolName,
        JsonElement args,
        AuditDecision decision,
        string reason,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(Audit(userId, toolName, args, decision, reason, conversationId, executionId: null, clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<ActionExecution?> ExecutionForAsync(Guid actionId, CancellationToken cancellationToken) =>
        db.ActionExecutions.AsNoTracking()
            .SingleOrDefaultAsync(execution => execution.PendingActionId == actionId, cancellationToken);

    /// <summary>
    /// The conditional write the whole design rests on. <c>requireOpen</c> is what makes approve,
    /// reject and expire mutually exclusive: the row moves only from <c>Pending</c>, and only before
    /// the deadline, so the loser observes a resolution rather than overwriting one.
    /// </summary>
    private Task<int> CompareAndSetAsync(
        Guid actionId,
        PendingActionStatus status,
        ActionResolutionReason reason,
        Guid? resolvedBy,
        bool requireOpen,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var query = db.PendingActions.Where(a => a.Id == actionId && a.Status == PendingActionStatus.Pending);

        if (requireOpen)
        {
            query = query.Where(a => a.ExpiresAt > now);
        }

        return query.ExecuteUpdateAsync(
            set => set
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.ResolutionReason, (ActionResolutionReason?)reason)
                .SetProperty(a => a.ResolvedByUserId, resolvedBy)
                .SetProperty(a => a.ResolvedAt, (DateTimeOffset?)now),
            cancellationToken);
    }

    /// <summary>
    /// Compares two fingerprints without leaking, through timing, how much of one is right. Length
    /// differences answer false rather than throwing, which is what a hash from an older algorithm
    /// looks like.
    /// </summary>
    private static bool Matches(string expected, string stored) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(stored));

    private Task<PendingAction?> ReloadAsync(Guid actionId, CancellationToken cancellationToken) =>
        db.PendingActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == actionId, cancellationToken);

    private static AuditEvent Audit(
        Guid userId,
        string toolName,
        JsonElement args,
        AuditDecision decision,
        string reason,
        Guid? conversationId,
        Guid? executionId,
        DateTimeOffset now) =>
        new()
        {
            Timestamp = now,
            UserId = userId,
            Action = "tool_call",
            ToolName = toolName,
            Decision = decision,
            ConversationId = conversationId,
            ExecutionId = executionId,
            PayloadJson = JsonSerializer.Serialize(new { args, reason }, Json),
        };
}
