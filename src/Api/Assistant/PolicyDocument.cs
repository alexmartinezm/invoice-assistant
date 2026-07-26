using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Domain;

namespace Api.Assistant;

public enum PolicyAction
{
    Allow,
    RequireConfirmation,
    Deny,
}

/// <summary>
/// One rule from <c>policies.json</c>. Every field except <see cref="Action"/> is an optional
/// condition, and a rule matches when <em>all</em> the conditions it does specify hold — a
/// conjunction of typed fields rather than an expression to parse (ADR 005).
/// </summary>
public sealed class PolicyRule
{
    public string? Tool { get; set; }

    /// <summary>Matched against the tool's declared metadata, never against its name.</summary>
    public string? SideEffect { get; set; }

    /// <summary>Exact role match.</summary>
    public string? Role { get; set; }

    /// <summary>Inclusive lower bound, using the <c>Viewer &lt; Accountant &lt; Admin</c> ordering.</summary>
    public string? MinRole { get; set; }

    /// <summary>
    /// Upper bound on the money at stake. Forces the engine to resolve the real amount before it
    /// can decide, which is why evaluation is not pure over the model's arguments.
    /// </summary>
    public decimal? MaxAmount { get; set; }

    /// <summary>
    /// Asserts the rule carries no role or amount condition — "this applies to everyone, always".
    /// Validated at load rather than trusted, so the claim cannot quietly become false.
    /// </summary>
    public bool Always { get; set; }

    public required string Action { get; set; }

    [JsonIgnore]
    public PolicyAction ParsedAction { get; private set; }

    [JsonIgnore]
    public Role? ParsedRole { get; private set; }

    [JsonIgnore]
    public Role? ParsedMinRole { get; private set; }

    [JsonIgnore]
    public ToolSideEffectFilter ParsedSideEffect { get; private set; }

    /// <summary>Human-readable identity for audit payloads and traces.</summary>
    public string Describe() =>
        $"{Tool ?? SideEffect ?? "any"}"
        + (Role is null ? string.Empty : $" role={Role}")
        + (MinRole is null ? string.Empty : $" minRole={MinRole}")
        + (MaxAmount is null ? string.Empty : $" maxAmount={MaxAmount}")
        + $" → {Action}";

    internal void Validate(int index)
    {
        ParsedAction = Action switch
        {
            "allow" => PolicyAction.Allow,
            "require_confirmation" => PolicyAction.RequireConfirmation,
            "deny" => PolicyAction.Deny,
            _ => throw new PolicyConfigurationException(
                $"rules[{index}].action is '{Action}'; expected allow, require_confirmation or deny."),
        };

        ParsedRole = ParseRole(Role, $"rules[{index}].role");
        ParsedMinRole = ParseRole(MinRole, $"rules[{index}].minRole");

        ParsedSideEffect = SideEffect switch
        {
            null => ToolSideEffectFilter.Any,
            "read" => ToolSideEffectFilter.Read,
            "write" => ToolSideEffectFilter.Write,
            _ => throw new PolicyConfigurationException(
                $"rules[{index}].sideEffect is '{SideEffect}'; expected read or write."),
        };

        if (Always && (ParsedRole is not null || ParsedMinRole is not null || MaxAmount is not null))
        {
            throw new PolicyConfigurationException(
                $"rules[{index}] sets always:true but also carries a role or amount condition, "
                + "so it does not in fact apply always. Drop the flag or drop the condition.");
        }
    }

    private static Role? ParseRole(string? value, string path) => value switch
    {
        null => null,
        _ when Enum.TryParse<Role>(value, ignoreCase: true, out var role) => role,
        _ => throw new PolicyConfigurationException(
            $"{path} is '{value}'; expected Viewer, Accountant or Admin."),
    };
}

public enum ToolSideEffectFilter
{
    Any,
    Read,
    Write,
}

public sealed class PolicyLimits
{
    /// <summary>The anti-injection brake: "cancel every invoice" dies here even if the model iterates.</summary>
    public int MaxWritesPerTurn { get; set; } = 1;

    public int MaxWritesPerConversation { get; set; } = 5;

    /// <summary>The loop brake: a model chaining reads without converging gets cut off.</summary>
    public int MaxToolCallsPerTurn { get; set; } = 8;
}

/// <summary>
/// <c>policies.json</c>, parsed and validated. Security rules live here rather than in the prompt,
/// because the prompt is UX and only the server can guarantee anything (ADR 001).
/// </summary>
public sealed class PolicyDocument
{
    public Dictionary<string, string> Defaults { get; set; } = [];

    public List<PolicyRule> Rules { get; set; } = [];

    public PolicyLimits Limits { get; set; } = new();

    [JsonIgnore]
    public PolicyAction DefaultForRead { get; private set; }

    [JsonIgnore]
    public PolicyAction DefaultForWrite { get; private set; }

    public static PolicyDocument Load(string path)
    {
        PolicyDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<PolicyDocument>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException exception)
        {
            throw new PolicyConfigurationException($"{path} is not valid JSON: {exception.Message}", exception);
        }

        if (document is null)
        {
            throw new PolicyConfigurationException($"{path} is empty.");
        }

        document.Validate();
        return document;
    }

    private void Validate()
    {
        // A missing default is not a small mistake: it decides what happens to a tool no rule
        // covers, so it fails at startup rather than silently picking one.
        DefaultForRead = ParseDefault("read");
        DefaultForWrite = ParseDefault("write");

        for (var index = 0; index < Rules.Count; index++)
        {
            Rules[index].Validate(index);
        }
    }

    private PolicyAction ParseDefault(string sideEffect) =>
        Defaults.TryGetValue(sideEffect, out var value)
            ? value switch
            {
                "allow" => PolicyAction.Allow,
                "require_confirmation" => PolicyAction.RequireConfirmation,
                "deny" => PolicyAction.Deny,
                _ => throw new PolicyConfigurationException(
                    $"defaults.{sideEffect} is '{value}'; expected allow, require_confirmation or deny."),
            }
            : throw new PolicyConfigurationException($"defaults.{sideEffect} is missing.");
}

public sealed class PolicyConfigurationException(string message, Exception? inner = null)
    : Exception(message, inner);
