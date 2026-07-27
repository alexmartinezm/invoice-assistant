using System.Security.Claims;
using System.Text.Json;
using Api.Assistant;
using Api.Assistant.Tools;
using Api.Domain;
using Api.Features.Auth;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Actions;

/// <summary>What became of a proposal, and the line the conversation closes on.</summary>
public sealed record ActionOutcome(
    Guid ActionId,
    string Status,
    string Summary,
    string Message,
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
public sealed record PendingActionView(
    Guid ActionId,
    string Tool,
    string Summary,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool Mine,
    bool CanApprove,
    string? RequiredRole);

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
/// The closing line is written here rather than by the model (ADR 007). The assistant is not asked
/// to narrate what happened to somebody's money — the server knows, and a sentence the server wrote
/// cannot be wrong about it.
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

        return Results.Ok(open.Select(action => ToView(action, principal, now)).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var action = await db.PendingActions.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (action is null || !MayResolve(principal, action))
        {
            return NotFound(id);
        }

        return Results.Ok(ToView(action, principal, clock.UtcNow));
    }

    /// <summary>
    /// Answers "what is this, and can you act on it?" in one place, so the list, the single fetch
    /// and the SSE event cannot come to different conclusions about the same row.
    /// </summary>
    private static PendingActionView ToView(PendingAction action, ClaimsPrincipal principal, DateTimeOffset now)
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
            RequiredRole: tool?.RequiredRole.ToString());
    }

    private static async Task<IResult> ApproveAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        ToolGate gate,
        IClock clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(ActionEndpoints));
        var approverId = principal.Id();
        var now = clock.UtcNow;

        var action = await db.PendingActions.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (action is null || !MayResolve(principal, action))
        {
            return NotFound(id);
        }

        if (!action.IsOpen(now))
        {
            await db.SaveChangesAsync(cancellationToken);
            return Closed(action, clock.UtcNow);
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

            return Results.Problem(
                title: "Action cannot be executed",
                detail: $"'{action.ToolName}' cannot be replayed by this build, so nothing was done. "
                    + "Ask the assistant again to propose it afresh.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "action_not_replayable" });
        }

        var decision = await gate.AuthoriseApprovalAsync(tool, args.RootElement, principal.Role(), cancellationToken);
        if (decision.Action is PolicyAction.Deny)
        {
            action.TryReject(approverId, now);
            await gate.AuditAsync(
                approverId, tool, args.RootElement, AuditDecision.Denied, decision.Reason, action.ConversationId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Problem(
                title: "Not permitted",
                detail: decision.Reason,
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["code"] = "policy_denied" });
        }

        // Claimed before it runs, and saved immediately: two tabs clicking approve at once means one
        // of them loses the race here rather than both reaching the API.
        if (!action.TryApprove(approverId, now))
        {
            await db.SaveChangesAsync(cancellationToken);
            return Closed(action, clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);

        // The action's own id is the idempotency key, so replaying an approval — a retry, a double
        // click that got past the claim — cannot perform the write twice.
        var result = await gate.ExecuteAsync(call, action.Id.ToString(), cancellationToken);
        var failed = result.ValueKind is JsonValueKind.Object && result.TryGetProperty("error", out _);

        await gate.AuditAsync(
            approverId,
            tool,
            args.RootElement,
            failed ? AuditDecision.Denied : AuditDecision.Confirmed,
            failed ? $"Approved, then refused by the API: {result.GetRawText()}" : decision.Reason,
            action.ConversationId,
            cancellationToken);

        if (failed)
        {
            // The layered-defence case: a person said yes and the server still said no. Worth
            // logging loudly, because it is the interesting one.
            logger.LogWarning("Approved action {ActionId} was refused by the API: {Result}", action.Id, result.GetRawText());
        }

        var message = failed
            ? $"{action.Summary} — approved, but the API refused it. Nothing changed."
            : $"Done: {action.Summary.ToLowerFirst()}.";

        await RecordClosingLineAsync(db, action, message, clock, cancellationToken);

        return Results.Ok(new ActionOutcome(
            action.Id, failed ? "failed" : "approved", action.Summary, message, result));
    }

    private static async Task<IResult> RejectAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var action = await db.PendingActions.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (action is null || !MayResolve(principal, action))
        {
            return NotFound(id);
        }

        if (!action.TryReject(principal.Id(), now))
        {
            await db.SaveChangesAsync(cancellationToken);
            return Closed(action, clock.UtcNow);
        }

        var message = $"Cancelled: {action.Summary.ToLowerFirst()}. Nothing was changed.";
        await RecordClosingLineAsync(db, action, message, clock, cancellationToken);

        return Results.Ok(new ActionOutcome(action.Id, "rejected", action.Summary, message, null));
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
        }

        await db.SaveChangesAsync(cancellationToken);
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

    private static IResult NotFound(Guid id) => Results.Problem(
        title: "Action not found",
        detail: $"There is no pending action with id '{id}'.",
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?> { ["code"] = "action_not_found" });

    private static string ToLowerFirst(this string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
