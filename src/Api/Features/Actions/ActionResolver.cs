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
    /// Closes a proposal without executing it. Used for a rejection and for a policy refusal at
    /// approval time, which are different reasons for the same absence of a write.
    /// </summary>
    public async Task<ActionClaim> ResolveWithoutExecutingAsync(
        PendingAction action,
        Guid? resolvedBy,
        PendingActionStatus status,
        ActionResolutionReason reason,
        CancellationToken cancellationToken)
    {
        using var activity = AssistantTelemetry.Source.StartActivity("assistant.action.resolve");
        activity?.SetTag("assistant.action_id", action.Id);
        activity?.SetTag("assistant.tool", action.ToolName);
        activity?.SetTag("assistant.decision", status.ToString().ToLowerInvariant());

        var claimed = await CompareAndSetAsync(
            action.Id, status, reason, resolvedBy, requireOpen: true, cancellationToken);

        var current = await ReloadAsync(action.Id, cancellationToken) ?? action;
        activity?.SetTag("assistant.claim", claimed == 1 ? "won" : "lost");

        return new ActionClaim(
            claimed == 1,
            current,
            claimed == 1 ? null : await ExecutionForAsync(action.Id, cancellationToken),
            claimed == 1 ? null : ClaimFailure.AlreadyResolved);
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
