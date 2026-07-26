using System.Text.Json;
using Api.Assistant;
using Api.Assistant.Tools;
using Api.Domain;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Tests;

/// <summary>
/// The matching table, as unit tests: no host, no database, no model. What is under test here is
/// the decision itself — which rule matches, in what order, and what happens when none does.
/// </summary>
public class ToolPolicyEngineTests
{
    private static readonly ToolIdentity Read = ReadToolCatalog.ListInvoices;
    private static readonly ToolIdentity Write = WriteToolCatalog.SendInvoice;
    private static readonly JsonElement NoArguments = JsonSerializer.SerializeToElement(new { });

    [Fact]
    public async Task Nothing_matching_falls_back_to_the_default_for_the_side_effect()
    {
        var engine = Engine();

        Assert.Equal(PolicyAction.Allow, (await Decide(engine, Read)).Action);
        Assert.Equal(PolicyAction.RequireConfirmation, (await Decide(engine, Write)).Action);
    }

    [Fact]
    public async Task The_first_matching_rule_wins()
    {
        var engine = Engine(
            """{ "tool": "send_invoice", "action": "allow" }""",
            """{ "sideEffect": "write", "action": "deny" }""");

        Assert.Equal(PolicyAction.Allow, (await Decide(engine, Write)).Action);

        var reversed = Engine(
            """{ "sideEffect": "write", "action": "deny" }""",
            """{ "tool": "send_invoice", "action": "allow" }""");

        Assert.Equal(PolicyAction.Deny, (await Decide(reversed, Write)).Action);
    }

    /// <summary>
    /// A rule keyed on <c>sideEffect</c> catches a tool it has never heard of. That is the property
    /// that makes adding a tool safe: it cannot escape an existing rule by being called something
    /// the rule does not name.
    /// </summary>
    [Fact]
    public async Task A_side_effect_rule_catches_every_tool_of_that_kind()
    {
        var engine = Engine("""{ "sideEffect": "write", "action": "deny" }""");

        foreach (var tool in WriteToolCatalog.All)
        {
            Assert.Equal(PolicyAction.Deny, (await Decide(engine, tool)).Action);
        }

        Assert.Equal(PolicyAction.Allow, (await Decide(engine, Read)).Action);
    }

    [Fact]
    public async Task Role_is_an_exact_match_and_min_role_is_a_floor()
    {
        var exact = Engine("""{ "sideEffect": "write", "role": "Accountant", "action": "deny" }""");

        Assert.Equal(PolicyAction.Deny, (await Decide(exact, Write, Role.Accountant)).Action);
        Assert.Equal(PolicyAction.RequireConfirmation, (await Decide(exact, Write, Role.Admin)).Action);
        Assert.Equal(PolicyAction.RequireConfirmation, (await Decide(exact, Write, Role.Viewer)).Action);

        var floor = Engine("""{ "sideEffect": "write", "minRole": "Accountant", "action": "allow" }""");

        Assert.Equal(PolicyAction.RequireConfirmation, (await Decide(floor, Write, Role.Viewer)).Action);
        Assert.Equal(PolicyAction.Allow, (await Decide(floor, Write, Role.Accountant)).Action);
        Assert.Equal(PolicyAction.Allow, (await Decide(floor, Write, Role.Admin)).Action);
    }

    /// <summary>
    /// The engine cannot resolve an amount without an invoice number to look up, so the rule does
    /// not match and the stricter default applies. Failing towards asking a human beats guessing.
    /// </summary>
    [Fact]
    public async Task An_amount_it_cannot_establish_does_not_satisfy_a_max_amount_rule()
    {
        var engine = Engine("""{ "tool": "send_invoice", "maxAmount": 100, "action": "allow" }""");

        var decision = await Decide(engine, Write);

        Assert.Equal(PolicyAction.RequireConfirmation, decision.Action);
        Assert.Null(decision.ResolvedAmount);
    }

    [Fact]
    public void An_always_rule_that_carries_a_condition_is_refused_at_load()
    {
        var exception = Assert.Throws<PolicyConfigurationException>(() =>
            Engine("""{ "tool": "cancel_invoice", "minRole": "Admin", "always": true, "action": "deny" }"""));

        Assert.Contains("does not in fact apply always", exception.Message);
    }

    [Fact]
    public void An_unknown_action_or_a_missing_default_is_refused_at_load()
    {
        Assert.Throws<PolicyConfigurationException>(() =>
            Engine("""{ "sideEffect": "write", "action": "maybe" }"""));

        Assert.Throws<PolicyConfigurationException>(() =>
            Load("""{ "defaults": { "read": "allow" }, "rules": [], "limits": {} }"""));
    }

    /// <summary>The shipped file has to load, or the app does not start.</summary>
    [Fact]
    public void The_repositorys_own_policy_file_loads()
    {
        var policy = Support.ApiFactory.Policy;

        Assert.Equal(PolicyAction.Allow, policy.DefaultForRead);
        Assert.Equal(PolicyAction.RequireConfirmation, policy.DefaultForWrite);
        Assert.True(policy.Limits.MaxWritesPerTurn >= 1);
    }

    private static Task<PolicyDecision> Decide(
        ToolPolicyEngine engine,
        ToolIdentity tool,
        Role role = Role.Accountant) =>
        engine.EvaluateAsync(tool, NoArguments, role, CancellationToken.None);

    private static ToolPolicyEngine Engine(params string[] rules) => Load(
        $$"""
        {
          "defaults": { "read": "allow", "write": "require_confirmation" },
          "rules": [{{string.Join(",", rules)}}],
          "limits": {}
        }
        """);

    private static ToolPolicyEngine Load(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"policies-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            // The context is never opened: no rule under test resolves an amount, and one that did
            // would fail loudly here rather than quietly matching.
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=policy-tests-never-connect")
                .Options);

            return new ToolPolicyEngine(PolicyDocument.Load(path), db, NullLogger<ToolPolicyEngine>.Instance);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
