using System.Security.Claims;
using System.Text.Json;
using Api.Assistant;
using Api.Assistant.Tools;
using Api.Domain;
using Api.Features.Auth;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Actions;

/// <summary>
/// What became of a proposal, and the line the conversation closes on.
/// </summary>
/// <remarks>
/// The decision and the execution are reported separately on purpose. "Approved" is a fact about a
/// person; "succeeded" is a fact about the world, and the gap between them is where a crash, a
/// refusal or an unconfirmed delivery lives. A single status would have to pick one of the two to
/// lie about.
/// </remarks>
/// <param name="DecisionStatus">approved, rejected, expired — the authorization outcome.</param>
/// <param name="ExecutionStatus">pending, executing, succeeded, failed, unknown — or null when nothing ran.</param>
public sealed record ActionOutcome(
    Guid ActionId,
    Guid? ExecutionId,
    string DecisionStatus,
    string? ExecutionStatus,
    string Summary,
    string Message,
    string? DeliveryStatus,
    JsonElement? Result);

/// <summary>
/// A proposal as somebody looking at it sees it.
/// </summary>
/// <param name="Mine">True when this caller is the one who proposed it.</param>
/// <param name="CanApprove">
/// Whether this caller clears the tool's role floor. Deliberately not a promise that approving will
/// succeed — policy is re-evaluated at approval time and the endpoint behind the tool has its own
/// limits, so a cleared floor can still end in a refusal (ADR 001). What it does guarantee is the
/// opposite: when it is false, approving cannot work, which is the case worth not offering a button
/// for.
/// </param>
/// <param name="RequiredRole">The role the tool needs, so a card can name who to escalate to.</param>
/// <param name="Execution">The attempt this decision authorized, once there is one.</param>
public sealed record PendingActionView(
    Guid ActionId,
    string Tool,
    string Summary,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool Mine,
    bool CanApprove,
    string? RequiredRole,
    ActionExecutionView? Execution);

/// <summary>
/// Approving or rejecting a write the assistant proposed.
/// </summary>
/// <remarks>
/// <para>
/// Approval runs inside the approver's own request, so the tool call goes out under the approver's
/// token exactly as a first-hand call would (ADR 002) and policy is re-evaluated against the
/// approver's role (ADR 001). The arguments come from the pending row, never from the request body:
/// the person agreed to a specific sentence, and that is what runs.
/// </para>
/// <para>
/// The resolution itself is a database compare-and-set, not an in-memory status check (ADR 009).
/// Whoever wins is bound as the actor and gets the single execution identity; everyone else is shown
/// the recorded outcome. A retry by the winner resumes or replays that same execution rather than
/// deciding again — which is what makes this endpoint safe to click twice, or to lose the response
/// from.
/// </para>
/// <para>
/// The closing line is written here rather than by the model (ADR 007), and only once the execution
/// settles. The assistant is not asked to narrate what happened to somebody's money — the server
/// knows, and a sentence the server wrote cannot be wrong about it.
/// </para>
/// </remarks>
public static class ActionEndpoints
{
    public static IEndpointRouteBuilder MapActionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/actions").WithTags("Assistant").RequireAuthorization();

        group.MapGet("/", OpenAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/approve", ApproveAsync);
        group.MapPost("/{id:guid}/reject", RejectAsync);

        return routes;
    }

    /// <summary>How many open proposals the list returns. A demo never has more; a cap is cheap.</summary>
    public const int MaxOpen = 50;

    /// <summary>
    /// The proposals still waiting on this caller.
    /// </summary>
    /// <remarks>
    /// Without this an escalation had nowhere to land. An Admin has always been allowed to resolve a
    /// proposal somebody else made — see <see cref="MayResolve"/> — but with no way to discover one
    /// and a five-minute window to do it in, that permission was unreachable in practice: the
    /// Accountant who proposed a cancellation could only watch their own card expire.
    /// </remarks>
    private static async Task<IResult> OpenAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var userId = principal.Id();
        var isAdmin = principal.Role() is Role.Admin;

        var open = await db.PendingActions.AsNoTracking()
            .Where(a => a.Status == PendingActionStatus.Pending && a.ExpiresAt > now)
            .Where(a => isAdmin || a.UserId == userId)
            // Soonest to lapse first: this is a queue with a clock on it, not a history.
            .OrderBy(a => a.ExpiresAt)
            .Take(MaxOpen)
            .ToListAsync(cancellationToken);

        return Results.Ok(open.Select(action => ToView(action, principal, now, execution: null)).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        ActionResolver resolver,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var action = await db.PendingActions.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (action is null || !MayResolve(principal, action))
        {
            return NotFound(id);
        }

        var execution = await resolver.ExecutionForAsync(id, cancellationToken);
        return Results.Ok(ToView(action, principal, clock.UtcNow, execution));
    }

    /// <summary>
    /// Answers "what is this, and can you act on it?" in one place, so the list, the single fetch
    /// and the SSE event cannot come to different conclusions about the same row.
    /// </summary>
    private static PendingActionView ToView(
        PendingAction action,
        ClaimsPrincipal principal,
        DateTimeOffset now,
        ActionExecution? execution)
    {
        var tool = WriteToolPlans.Find(action.ToolName);
        var role = principal.Role();

        return new PendingActionView(
            action.Id,
            action.ToolName,
            action.Summary,
            Describe(action, now),
            action.CreatedAt,
            action.ExpiresAt,
            Mine: action.UserId == principal.Id(),
            CanApprove: action.IsOpen(now) && tool is not null && role >= tool.RequiredRole,
            RequiredRole: tool?.RequiredRole.ToString(),
            Execution: execution is null ? null : ActionExecutionView.Of(execution));
    }

    private static async Task<IResult> ApproveAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        ToolGate gate,
        ActionResolver resolver,
        ActionExecutor executor,
        IClock clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(ActionEndpoints));
        var approverId = principal.Id();

        var action = await db.PendingActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (action is null || !MayResolve(principal, action))
        {
            return NotFound(id);
        }

        using var args = JsonDocument.Parse(action.ArgsJson);

        ToolIdentity tool;
        ToolCall call;

        try
        {
            (tool, call) = WriteToolPlans.Replay(action.ToolName, args.RootElement);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // The row names a tool this build cannot rebuild a request for: a deployment that
            // changed underneath an open approval, or a policy that queued something with no write
            // plan (a read under require_confirmation). Refusing is the only safe answer — executing
            // a guess is exactly what the approval exists to prevent — and it says so rather than
            // surfacing as an unexplained 500.
            logger.LogError(exception, "Pending action {ActionId} for {Tool} cannot be replayed", action.Id, action.ToolName);
            return NotReplayable(action);
        }

        // Already decided by this caller: resume or replay, never decide again. This is the branch
        // that makes a lost response harmless — the retry lands here and finds its own execution.
        if (action.Status is PendingActionStatus.Approved)
        {
            var existing = await resolver.ExecutionForAsync(action.Id, cancellationToken);

            if (existing is null || action.ResolvedByUserId != approverId)
            {
                // A different approver won, or the row was resolved outside the resolver. Neither is
                // this caller's execution to continue under their own identity.
                return Closed(action, clock.UtcNow);
            }

            return await ContinueAsync(action, existing, call, db, executor, clock, logger, cancellationToken);
        }

        if (!action.IsOpen(clock.UtcNow))
        {
            await resolver.MarkExpiredAsync(action.Id, cancellationToken);
            return Closed(
                await db.PendingActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken) ?? action,
                clock.UtcNow);
        }

        var decision = await gate.AuthoriseApprovalAsync(tool, args.RootElement, principal.Role(), cancellationToken);
        if (decision.Action is PolicyAction.Deny)
        {
            await resolver.ResolveWithoutExecutingAsync(
                action, approverId, PendingActionStatus.Rejected, ActionResolutionReason.PolicyDenied, cancellationToken);
            await resolver.AuditAsync(
                approverId, tool.Name, args.RootElement, AuditDecision.Denied, decision.Reason, action.ConversationId, cancellationToken);

            return Results.Problem(
                title: "Not permitted",
                detail: decision.Reason,
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["code"] = "policy_denied" });
        }

        var claim = await resolver.ClaimForApprovalAsync(
            action, args.RootElement, approverId, decision.Reason, cancellationToken);

        if (!claim.Won)
        {
            if (claim.Failure is ClaimFailure.NotFingerprinted)
            {
                return NotReplayable(claim.Action);
            }

            // Lost the race. If this caller is the one who won it a moment ago — two tabs, one
            // person — continue their execution; otherwise report the recorded outcome.
            return claim.Action.ResolvedByUserId == approverId && claim.Execution is { } mine
                ? await ContinueAsync(claim.Action, mine, call, db, executor, clock, logger, cancellationToken)
                : Closed(claim.Action, clock.UtcNow);
        }

        return await ContinueAsync(claim.Action, claim.Execution!, call, db, executor, clock, logger, cancellationToken);
    }

    /// <summary>
    /// Takes an authorized execution as far as it can go, and reports where that is.
    /// </summary>
    /// <remarks>
    /// Three cases, and the distinction between them is the whole feature. A settled execution is
    /// <em>replayed</em> from its stored result — no second call, no second closing line. One that
    /// is in flight elsewhere is reported as-is rather than raced. Only a fresh or unconfirmed
    /// execution actually attempts anything, and it does so under the same idempotency key it has
    /// always had.
    /// </remarks>
    private static async Task<IResult> ContinueAsync(
        PendingAction action,
        ActionExecution execution,
        ToolCall call,
        AppDbContext db,
        ActionExecutor executor,
        IClock clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Settled, or already in flight on another request: report it, do not race it.
        if (execution.IsSettled || execution.Status is ActionExecutionStatus.Executing)
        {
            return Outcome(action, execution, Message(action, execution), StoredResult(execution), clock.UtcNow);
        }

        var attempt = await executor.RunAsync(execution, call, action.ExpectedResourceRevision, cancellationToken);
        var settled = attempt.Execution;

        if (settled.Status is ActionExecutionStatus.Failed)
        {
            // The layered-defence case: a person said yes and the server still said no. Worth
            // logging loudly, because it is the interesting one.
            logger.LogWarning(
                "Approved action {ActionId} was refused by the API: {Code}", action.Id, settled.ErrorCode);
        }

        var message = Message(action, settled);

        // The transcript gets a line only once the outcome is known. An assistant message saying
        // "Done" while an execution is still unconfirmed would be exactly the kind of claim this
        // whole slice exists to stop making.
        if (settled.IsSettled)
        {
            await RecordClosingLineAsync(db, action, message, clock, cancellationToken);
        }

        return Outcome(action, settled, message, attempt.Payload, clock.UtcNow);
    }

    private static async Task<IResult> RejectAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        ActionResolver resolver,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var action = await db.PendingActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (action is null || !MayResolve(principal, action))
        {
            return NotFound(id);
        }

        var claim = await resolver.ResolveWithoutExecutingAsync(
            action, principal.Id(), PendingActionStatus.Rejected, ActionResolutionReason.UserRejected, cancellationToken);

        if (!claim.Won)
        {
            // The loser of an approve/reject race is shown what was decided, not given a second
            // decision. Rejecting an action that already executed would be a lie on the card.
            return Closed(claim.Action, clock.UtcNow);
        }

        var message = $"Cancelled: {action.Summary.ToLowerFirst()}. Nothing was changed.";
        await RecordClosingLineAsync(db, action, message, clock, cancellationToken);

        return Results.Ok(new ActionOutcome(
            action.Id, null, "rejected", null, action.Summary, message, null, null));
    }

    /// <summary>
    /// The sentence the user is shown, in the server's words. Five outcomes, five sentences: the
    /// copy is the only place a person learns that "approved" and "done" are not the same thing.
    /// </summary>
    private static string Message(PendingAction action, ActionExecution execution) => execution switch
    {
        { Status: ActionExecutionStatus.Succeeded } => $"Done: {action.Summary.ToLowerFirst()}.",

        // Its own sentence, because it is not a refusal and telling the user "the API said no" would
        // send them looking for a permission problem. What happened is that the thing they were
        // shown moved on, and the only honest answer is to ask again against what it is now.
        { ErrorCode: "resource_changed" } =>
            $"{action.Summary} — not done: the invoice changed after this was proposed, so the approval no "
                + "longer matches what you were shown. Ask again to see the current state.",

        { Status: ActionExecutionStatus.Failed } =>
            $"{action.Summary} — approved, but the API refused it. Nothing changed.",
        { Status: ActionExecutionStatus.Unknown } =>
            $"{action.Summary} — approved. The outcome is not confirmed yet; it is being reconciled.",
        _ => $"{action.Summary} — approved and running.",
    };

    private static IResult Outcome(
        PendingAction action,
        ActionExecution execution,
        string message,
        JsonElement? result,
        DateTimeOffset now) =>
        Results.Json(
            new ActionOutcome(
                action.Id,
                execution.Id,
                Describe(action, now),
                execution.Status.ToString().ToLowerInvariant(),
                action.Summary,
                message,
                DeliveryStatus: null,
                result),
            // 202 while the answer is still owed: a client that treats 200 as "finished" is right to.
            statusCode: execution.IsSettled ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);

    private static JsonElement? StoredResult(ActionExecution execution)
    {
        if (execution.ResultJson is not { Length: > 0 } json)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Appends the outcome to the conversation as an assistant message, so the next turn's history
    /// contains what actually happened rather than a proposal that trails off.
    /// </summary>
    private static async Task RecordClosingLineAsync(
        AppDbContext db,
        PendingAction action,
        string message,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (action.ConversationId is { } conversationId)
        {
            db.Set<Message>().Add(new Message
            {
                ConversationId = conversationId,
                Role = MessageRole.Assistant,
                Content = message,
                CreatedAt = clock.UtcNow,
            });

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Whoever proposed it, or an Admin. The role needed to <em>run</em> the tool is checked
    /// separately at approval time; this is only about who can see the proposal at all.
    /// </summary>
    private static bool MayResolve(ClaimsPrincipal principal, PendingAction action) =>
        action.UserId == principal.Id() || principal.Role() is Role.Admin;

    private static string Describe(PendingAction action, DateTimeOffset now) =>
        action.IsOpen(now) ? "pending" : action.Status.ToString().ToLowerInvariant();

    private static IResult Closed(PendingAction action, DateTimeOffset now) => Results.Problem(
        title: "Action is no longer open",
        detail: $"This action is {Describe(action, now)}. Ask the assistant again if you still want it done.",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "action_not_open",
            ["status"] = Describe(action, now),
        });

    private static IResult NotReplayable(PendingAction action) => Results.Problem(
        title: "Action cannot be executed",
        detail: $"'{action.ToolName}' cannot be replayed by this build, so nothing was done. "
            + "Ask the assistant again to propose it afresh.",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?> { ["code"] = "action_not_replayable" });

    private static IResult NotFound(Guid id) => Results.Problem(
        title: "Action not found",
        detail: $"There is no pending action with id '{id}'.",
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?> { ["code"] = "action_not_found" });

    private static string ToLowerFirst(this string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
