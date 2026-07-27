using System.Security.Claims;
using System.Text.Json;
using Api.Domain;
using Api.Features.Auth;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Assistant.Tools;

/// <summary>
/// The one place every tool call passes through: per-turn limits, then policy, then execution.
/// </summary>
/// <remarks>
/// <para>
/// The gate sits inside the tool delegates rather than in <see cref="ChatOrchestrator"/> because
/// <c>Microsoft.Extensions.AI</c>'s function-invoking middleware owns execution — the orchestrator
/// only watches it happen. Putting the gate here means the model has no path around it: refusing is
/// not a behaviour the prompt asks for, it is a branch this code takes.
/// </para>
/// <para>
/// Reads pass through too. That is what makes <c>defaults.read</c> in <c>policies.json</c> mean
/// something: a deployment can require confirmation for reads, or deny them for a role, without a
/// code change.
/// </para>
/// </remarks>
public sealed class ToolGate(
    SelfApiClient api,
    ToolPolicyEngine policy,
    TurnJournal journal,
    AppDbContext db,
    IClock clock,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ToolGate> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How long a proposal stays approvable. Short on purpose — see <see cref="PendingAction"/>.</summary>
    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromMinutes(5);

    /// <param name="describe">
    /// Builds the sentence the user is asked to approve, from data the server resolved. The model
    /// proposes the action; it does not get to write what the approval says.
    /// </param>
    public async Task<JsonElement> RunAsync(
        ToolIdentity tool,
        object arguments,
        Func<CancellationToken, Task<string>> describe,
        ToolCall call,
        CancellationToken cancellationToken)
    {
        // One span per tool call, carrying what the gate decided. The policy engine has a span of
        // its own, but a call stopped by a per-turn limit never reaches the engine — without this
        // span a blocked call would be the one thing a trace could not explain.
        using var activity = AssistantTelemetry.Source.StartActivity("assistant.tool_call");
        activity?.SetTag("assistant.tool", tool.Name);
        activity?.SetTag("assistant.side_effect", tool.SideEffect.ToString());

        journal.CountToolCall();

        var principal = httpContextAccessor.HttpContext?.User
            ?? throw new InvalidOperationException("A tool ran outside an authenticated request.");
        var role = principal.Role();
        var userId = principal.Id();
        var args = JsonSerializer.SerializeToElement(arguments, Json);

        // Limits first, then policy. The per-turn brake has to fire even for a call policy would
        // happily allow, because what it defends against is a model talked into doing something
        // permitted forty times over.
        if (await ExceedsLimitsAsync(tool, userId, args, cancellationToken) is { } blocked)
        {
            activity?.SetTag("assistant.gate_decision", "blocked");
            return blocked;
        }

        var decision = await policy.EvaluateAsync(tool, args, role, cancellationToken);

        switch (decision.Action)
        {
            case PolicyAction.Deny:
                activity?.SetTag("assistant.gate_decision", "denied");
                logger.LogWarning("Denied {Tool}: {Reason}", tool.Name, decision.Reason);
                await AuditAsync(userId, tool, args, AuditDecision.Denied, decision.Reason, journal.ConversationId, cancellationToken);
                journal.Record(new ToolBlocked(tool.Name, decision.Reason));
                return Error("policy_denied", decision.Reason);

            case PolicyAction.RequireConfirmation:
                activity?.SetTag("assistant.gate_decision", "pending_approval");
                return await ProposeAsync(tool, userId, args, describe, decision, cancellationToken);

            default:
                activity?.SetTag("assistant.gate_decision", "allowed");

                // A fresh key per attempt: it makes the call safe to retry inside the HTTP stack,
                // which is what it is for. An approval replays under the pending action's own id.
                var result = await ExecuteAsync(call, Guid.CreateVersion7().ToString(), cancellationToken);

                if (tool.SideEffect is ToolSideEffect.Write)
                {
                    journal.CountWrite();
                    await AuditAsync(userId, tool, args, AuditDecision.Auto, decision.Reason, journal.ConversationId, cancellationToken);
                }

                return result;
        }
    }

    /// <summary>
    /// Runs a call a human has just approved, under that human's identity.
    /// </summary>
    /// <remarks>
    /// Policy is evaluated again here rather than trusted from proposal time (ADR 001): the approver
    /// may not be the proposer, and a rule or a role may have changed in between. A <c>deny</c> now
    /// beats an <c>allow</c> five minutes ago.
    /// </remarks>
    public async Task<PolicyDecision> AuthoriseApprovalAsync(
        ToolIdentity tool,
        JsonElement args,
        Role approverRole,
        CancellationToken cancellationToken)
    {
        if (approverRole < tool.RequiredRole)
        {
            return new PolicyDecision(
                PolicyAction.Deny,
                $"{tool.Name} needs the {tool.RequiredRole} role; the approver is {approverRole}.");
        }

        var decision = await policy.EvaluateAsync(tool, args, approverRole, cancellationToken);

        // require_confirmation is exactly what is happening: a person is confirming it. Only a
        // deny stops the action here.
        return decision.Action is PolicyAction.Deny
            ? decision
            : decision with { Action = PolicyAction.Allow };
    }

    public Task<JsonElement> ExecuteAsync(ToolCall call, string idempotencyKey, CancellationToken cancellationToken) =>
        call.Method == HttpMethod.Get
            ? api.GetJsonAsync(call.Url, cancellationToken)
            : api.SendJsonAsync(call.Method, call.Url, call.Body, idempotencyKey, cancellationToken);

    /// <summary>
    /// Records what the gate decided. Writes are always audited; an allowed read is not, so that
    /// <see cref="AuditDecision.Auto"/> keeps its one meaning — a change that happened without a
    /// human in the loop — which is exactly what <c>evals/cases/injection-03.yaml</c> asserts on.
    /// A read the gate refused is still audited: that one is a security event.
    /// </summary>
    public async Task AuditAsync(
        Guid userId,
        ToolIdentity tool,
        JsonElement args,
        AuditDecision decision,
        string reason,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Timestamp = clock.UtcNow,
            UserId = userId,
            Action = "tool_call",
            ToolName = tool.Name,
            Decision = decision,
            ConversationId = conversationId,
            PayloadJson = JsonSerializer.Serialize(new { args, reason }, Json),
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<JsonElement?> ExceedsLimitsAsync(
        ToolIdentity tool,
        Guid userId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        var limits = policy.Limits;

        if (journal.ToolCalls > limits.MaxToolCallsPerTurn)
        {
            return await BlockAsync(
                tool, userId, args,
                $"This turn has already used its {limits.MaxToolCallsPerTurn} tool calls. Ask the user to narrow the request.",
                cancellationToken);
        }

        if (tool.SideEffect is not ToolSideEffect.Write)
        {
            return null;
        }

        // The anti-injection brake: "cancel every invoice" dies here even if the model iterates.
        // Proposals count as well as executions — see TurnJournal.WritesPlanned.
        if (journal.WritesPlanned >= limits.MaxWritesPerTurn)
        {
            return await BlockAsync(
                tool, userId, args,
                $"Only {limits.MaxWritesPerTurn} change per turn is allowed. Ask the user to confirm the next one separately.",
                cancellationToken);
        }

        if (journal.ConversationId is { } conversationId)
        {
            var executed = await db.AuditEvents.CountAsync(
                audit => audit.ConversationId == conversationId
                    && (audit.Decision == AuditDecision.Auto || audit.Decision == AuditDecision.Confirmed),
                cancellationToken);

            if (executed >= limits.MaxWritesPerConversation)
            {
                return await BlockAsync(
                    tool, userId, args,
                    $"This conversation has already made its {limits.MaxWritesPerConversation} changes. Start a new one to continue.",
                    cancellationToken);
            }
        }

        return null;
    }

    private async Task<JsonElement> BlockAsync(
        ToolIdentity tool,
        Guid userId,
        JsonElement args,
        string reason,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("Blocked {Tool}: {Reason}", tool.Name, reason);
        await AuditAsync(userId, tool, args, AuditDecision.Blocked, reason, journal.ConversationId, cancellationToken);
        journal.Record(new ToolBlocked(tool.Name, reason));
        return Error("limit_reached", reason);
    }

    private async Task<JsonElement> ProposeAsync(
        ToolIdentity tool,
        Guid userId,
        JsonElement args,
        Func<CancellationToken, Task<string>> describe,
        PolicyDecision decision,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var action = new PendingAction
        {
            UserId = userId,
            ConversationId = journal.ConversationId,
            ToolName = tool.Name,
            ArgsJson = args.GetRawText(),
            Summary = await describe(cancellationToken),
            CreatedAt = now,
            ExpiresAt = now + ApprovalWindow,
        };

        db.PendingActions.Add(action);
        await db.SaveChangesAsync(cancellationToken);

        // Only a write spends the write budget. A deployment that sets defaults.read to
        // require_confirmation gets its reads queued for a person without one confirmed read
        // silently consuming the turn's single allowed change.
        if (tool.SideEffect is ToolSideEffect.Write)
        {
            journal.CountProposal();
        }

        journal.Record(new ApprovalRequired(action.Id, tool.Name, action.Summary, action.ExpiresAt));

        // The turn continues (ADR 007): the model gets a result it can narrate in one line, and the
        // approval card arrives beside it. Nothing has changed in the database.
        return JsonSerializer.SerializeToElement(
            new
            {
                status = "pending_approval",
                actionId = action.Id,
                summary = action.Summary,
                expiresAt = action.ExpiresAt,
                reason = decision.Reason,
                note = "Nothing has changed yet. Say in one line that this is waiting for approval, then stop.",
            },
            Json);
    }

    private static JsonElement Error(string error, string reason) =>
        JsonSerializer.SerializeToElement(new { error, reason }, Json);
}
