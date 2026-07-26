using System.Diagnostics;
using System.Text.Json;
using Api.Assistant.Tools;
using Api.Domain;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Assistant;

/// <summary>
/// What the gate decided, and why. The reason is written to be read by two audiences: the model,
/// which has to explain a refusal to the user, and the audit trail.
/// </summary>
public sealed record PolicyDecision(
    PolicyAction Action,
    string Reason,
    string? MatchedRule = null,
    decimal? ResolvedAmount = null);

/// <summary>
/// Decides whether a tool call may execute, be proposed for approval, or be refused.
/// </summary>
/// <remarks>
/// <para>
/// Rules are evaluated in order and the first match wins; if none matches, the default for the
/// tool's side effect applies. Matching is a conjunction of typed fields — no expression language
/// to parse and secure (ADR 005) — and it reads the tool's declared metadata rather than its name,
/// so a new tool cannot slip past a rule by being called something else.
/// </para>
/// <para>
/// Evaluation is deliberately not pure over the model's arguments. A <c>maxAmount</c> rule has to
/// know the real total, so the engine looks the invoice up itself before deciding.
/// </para>
/// </remarks>
public sealed class ToolPolicyEngine(
    PolicyDocument policy,
    AppDbContext db,
    ILogger<ToolPolicyEngine> logger)
{
    public PolicyLimits Limits => policy.Limits;

    public async Task<PolicyDecision> EvaluateAsync(
        ToolIdentity tool,
        JsonElement arguments,
        Role role,
        CancellationToken cancellationToken)
    {
        using var activity = AssistantTelemetry.Source.StartActivity("assistant.policy_evaluate");
        activity?.SetTag("policy.tool", tool.Name);
        activity?.SetTag("policy.side_effect", tool.SideEffect.ToString());
        activity?.SetTag("policy.role", role.ToString());

        for (var index = 0; index < policy.Rules.Count; index++)
        {
            var rule = policy.Rules[index];
            var (matches, amount) = await MatchesAsync(rule, tool, arguments, role, cancellationToken);

            if (!matches)
            {
                continue;
            }

            var decision = new PolicyDecision(
                rule.ParsedAction,
                DescribeOutcome(rule.ParsedAction, tool, rule.Describe()),
                rule.Describe(),
                amount);

            activity?.SetTag("policy.decision", decision.Action.ToString());
            activity?.SetTag("policy.matched_rule", $"rules[{index}]");
            logger.LogInformation(
                "Policy {Decision} for {Tool} via rules[{Index}]", decision.Action, tool.Name, index);

            return decision;
        }

        var fallback = tool.SideEffect is ToolSideEffect.Write
            ? policy.DefaultForWrite
            : policy.DefaultForRead;

        activity?.SetTag("policy.decision", fallback.ToString());
        activity?.SetTag("policy.matched_rule", "defaults");
        logger.LogInformation("Policy {Decision} for {Tool} via defaults", fallback, tool.Name);

        return new PolicyDecision(fallback, DescribeOutcome(fallback, tool, "the default for its side effect"));
    }

    private async Task<(bool Matches, decimal? Amount)> MatchesAsync(
        PolicyRule rule,
        ToolIdentity tool,
        JsonElement arguments,
        Role role,
        CancellationToken cancellationToken)
    {
        if (rule.Tool is not null && !string.Equals(rule.Tool, tool.Name, StringComparison.Ordinal))
        {
            return (false, null);
        }

        if (rule.ParsedSideEffect is not ToolSideEffectFilter.Any
            && rule.ParsedSideEffect.ToString() != tool.SideEffect.ToString())
        {
            return (false, null);
        }

        if (rule.ParsedRole is { } exact && role != exact)
        {
            return (false, null);
        }

        if (rule.ParsedMinRole is { } minimum && role < minimum)
        {
            return (false, null);
        }

        if (rule.MaxAmount is not { } ceiling)
        {
            return (true, null);
        }

        var amount = await ResolveAmountAsync(arguments, cancellationToken);

        // An amount we cannot establish means the rule does not match, so evaluation falls through
        // to the stricter defaults. Failing toward asking a human beats guessing and executing.
        return amount is { } resolved ? (resolved <= ceiling, resolved) : (false, null);
    }

    /// <summary>
    /// Looks the invoice up rather than trusting a number in the arguments. This is the engine's
    /// own context resolution, part of its contract (ADR 005), and it reads the database directly:
    /// the gate is server-side infrastructure deciding whether a tool may run at all, not another
    /// client of the API the way the tools themselves are (ADR 002).
    /// </summary>
    private async Task<decimal?> ResolveAmountAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind is not JsonValueKind.Object
            || !arguments.TryGetProperty("number", out var number)
            || number.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        using var activity = AssistantTelemetry.Source.StartActivity("assistant.policy_resolve_amount");
        activity?.SetTag("policy.invoice_number", number.GetString());

        var total = await db.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.Number == number.GetString())
            .Select(invoice => (decimal?)invoice.Total)
            .SingleOrDefaultAsync(cancellationToken);

        activity?.SetTag("policy.resolved_amount", total);
        return total;
    }

    private static string DescribeOutcome(PolicyAction action, ToolIdentity tool, string rule) => action switch
    {
        PolicyAction.Allow => $"{tool.Name} is allowed by policy ({rule}).",
        PolicyAction.RequireConfirmation =>
            $"{tool.Name} needs a person to approve it before it can run ({rule}).",
        PolicyAction.Deny => $"{tool.Name} is not permitted for this user ({rule}).",
        _ => rule,
    };
}
