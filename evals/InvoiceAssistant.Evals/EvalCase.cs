using YamlDotNet.Serialization;

namespace InvoiceAssistant.Evals;

/// <summary>One acceptable tool call: a name, plus argument values that must all be present.</summary>
public sealed record ToolExpectation(string Tool, IReadOnlyDictionary<string, string?> ArgsMatch);

/// <summary>
/// What a case asserts. Every field is a fact observable from outside the model — recorded tool
/// calls, gate decisions, rows in the database — never the free-form response text, which belongs
/// to the judge level (see docs/ai/evaluation.md).
/// </summary>
public sealed class EvalExpectation
{
    /// <summary>At least one of these calls must have been made. Empty means no tool assertion.</summary>
    public List<ToolExpectation> AnyOf { get; init; } = [];

    /// <summary>No write tool was called at all — proposed, executed or refused.</summary>
    public bool NoWriteTools { get; init; }

    /// <summary>No tool was called at all. For out-of-scope requests the right move is to decline.</summary>
    public bool NoTools { get; init; }

    /// <summary>
    /// Ceiling on how many tools the turn may call. Null means no ceiling.
    /// </summary>
    /// <remarks>
    /// This asserts the shape of the answer, not just its ingredients. A model that cannot reach a
    /// figure in one call will enumerate towards it, hit the per-turn brake in
    /// <c>policies.json</c>, and then answer from whatever partial scan it managed — which reads
    /// exactly like a complete answer. <c>tool_called</c> alone cannot catch that: the right tool
    /// was called, it was just called first and then abandoned.
    /// </remarks>
    public int? MaxToolCalls { get; init; }

    /// <summary>Exact number of invoices actually created or changed in the database.</summary>
    public int? WritesExecuted { get; init; }

    /// <summary>True when the case says anything about pending actions, including "none".</summary>
    public bool PendingActionSpecified { get; init; }

    /// <summary>The tool a new <c>PendingAction</c> must name; null (when specified) means none created.</summary>
    public string? PendingActionTool { get; init; }

    /// <summary>Gate decisions that must appear among this turn's new audit events.</summary>
    public List<string> AuditContains { get; init; } = [];

    /// <summary>Gate decisions that must not appear among this turn's new audit events.</summary>
    public List<string> AuditNotContains { get; init; } = [];

    /// <summary>
    /// Exact number of durable execution rows the turn created, when the case says.
    /// </summary>
    /// <remarks>
    /// Stronger than <see cref="WritesExecuted"/> for the injection cases. A write gets an execution
    /// the moment it is <em>attempted</em>, before the API has had a chance to refuse it — so
    /// <c>executions_created: 0</c> asserts the assistant never tried, not merely that nothing
    /// landed. The two failure modes look identical in the invoice table and are not the same thing.
    /// </remarks>
    public int? ExecutionsCreated { get; init; }
}

public sealed record EvalCase(
    string Id,
    string Category,
    string User,
    string Prompt,
    EvalExpectation Expect,
    string FilePath);

/// <summary>
/// Loads and validates <c>evals/cases/*.yaml</c>. Strict on purpose: an unknown expectation key is
/// a typo that would otherwise pass silently forever, which in a safety suite is worse than a
/// build break.
/// </summary>
public static class EvalCases
{
    private static readonly string[] Categories =
    [
        "read", "write-propose", "permissions", "injection", "calculation", "domain-errors", "out-of-scope",
    ];

    private static readonly string[] ExpectationKeys =
    [
        "any_of", "tool_called", "args_match", "no_write_tools", "no_tools", "max_tool_calls",
        "writes_executed", "pending_action_created", "audit_contains", "audit_not_contains",
        "executions_created",
    ];

    public static IReadOnlyDictionary<string, EvalCase> All { get; } = Load();

    private static Dictionary<string, EvalCase> Load()
    {
        var directory = EvalEnvironment.Current.CasesDirectory;
        var files = Directory.GetFiles(directory, "*.yaml").OrderBy(f => f, StringComparer.Ordinal).ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No eval cases found under {directory}.");
        }

        var deserializer = new DeserializerBuilder().Build();
        Dictionary<string, EvalCase> cases = [];

        foreach (var file in files)
        {
            var root = deserializer.Deserialize<Dictionary<object, object?>>(File.ReadAllText(file))
                ?? throw new InvalidOperationException($"{file} is empty.");

            var evalCase = Parse(root, file);

            if (!cases.TryAdd(evalCase.Id, evalCase))
            {
                throw new InvalidOperationException($"Duplicate case id '{evalCase.Id}' ({file}).");
            }
        }

        return cases;
    }

    private static EvalCase Parse(Dictionary<object, object?> root, string file)
    {
        var id = RequiredText(root, "id", file);
        var category = RequiredText(root, "category", file);

        if (!Categories.Contains(category))
        {
            throw new InvalidOperationException(
                $"{file}: category '{category}' is not one of: {string.Join(", ", Categories)}.");
        }

        if (root.GetValueOrDefault("expect") is not Dictionary<object, object?> expect)
        {
            throw new InvalidOperationException($"{file}: 'expect' is missing or not a mapping.");
        }

        foreach (var key in expect.Keys.Select(k => k.ToString()!))
        {
            if (!ExpectationKeys.Contains(key))
            {
                throw new InvalidOperationException(
                    $"{file}: unknown expectation '{key}'. Known: {string.Join(", ", ExpectationKeys)}.");
            }
        }

        return new EvalCase(
            id,
            category,
            RequiredText(root, "user", file),
            RequiredText(root, "prompt", file),
            ParseExpectation(expect, file),
            file);
    }

    private static EvalExpectation ParseExpectation(Dictionary<object, object?> expect, string file)
    {
        List<ToolExpectation> anyOf = [];

        if (expect.GetValueOrDefault("any_of") is List<object?> alternatives)
        {
            foreach (var alternative in alternatives)
            {
                if (alternative is not Dictionary<object, object?> option)
                {
                    throw new InvalidOperationException($"{file}: every any_of entry must be a mapping.");
                }

                anyOf.Add(ParseToolExpectation(option, file));
            }
        }

        // A single tool_called at the top level is shorthand for a one-entry any_of.
        if (Text(expect.GetValueOrDefault("tool_called")) is not null)
        {
            anyOf.Add(ParseToolExpectation(expect, file));
        }

        return new EvalExpectation
        {
            AnyOf = anyOf,
            NoWriteTools = Flag(expect, "no_write_tools", file),
            NoTools = Flag(expect, "no_tools", file),
            MaxToolCalls = Text(expect.GetValueOrDefault("max_tool_calls")) is { } maxToolCalls
                ? int.Parse(maxToolCalls)
                : null,
            WritesExecuted = Text(expect.GetValueOrDefault("writes_executed")) is { } writes
                ? int.Parse(writes)
                : null,
            PendingActionSpecified = expect.ContainsKey("pending_action_created"),
            PendingActionTool = Text(expect.GetValueOrDefault("pending_action_created")),
            AuditContains = Texts(expect.GetValueOrDefault("audit_contains"), file, "audit_contains"),
            AuditNotContains = Texts(expect.GetValueOrDefault("audit_not_contains"), file, "audit_not_contains"),
            ExecutionsCreated = Text(expect.GetValueOrDefault("executions_created")) is { } executions
                ? int.Parse(executions)
                : null,
        };
    }

    private static ToolExpectation ParseToolExpectation(Dictionary<object, object?> option, string file)
    {
        var tool = Text(option.GetValueOrDefault("tool_called"))
            ?? throw new InvalidOperationException($"{file}: a tool expectation needs 'tool_called'.");

        Dictionary<string, string?> args = [];

        if (option.GetValueOrDefault("args_match") is Dictionary<object, object?> match)
        {
            foreach (var (key, value) in match)
            {
                args[key.ToString()!] = Text(value);
            }
        }

        return new ToolExpectation(tool, args);
    }

    private static bool Flag(Dictionary<object, object?> mapping, string key, string file) =>
        Text(mapping.GetValueOrDefault(key)) switch
        {
            null => false,
            "true" => true,
            "false" => false,
            var other => throw new InvalidOperationException($"{file}: {key} is '{other}'; expected true or false."),
        };

    private static List<string> Texts(object? value, string file, string key) =>
        value switch
        {
            null => [],
            List<object?> list => [.. list.Select(item => Text(item)
                ?? throw new InvalidOperationException($"{file}: {key} contains a null entry."))],
            _ => throw new InvalidOperationException($"{file}: {key} must be a list."),
        };

    private static string RequiredText(Dictionary<object, object?> mapping, string key, string file) =>
        Text(mapping.GetValueOrDefault(key))
        ?? throw new InvalidOperationException($"{file}: '{key}' is missing.");

    /// <summary>Untyped YAML scalars arrive as strings; a bare <c>null</c> or <c>~</c> means null.</summary>
    private static string? Text(object? value) =>
        value?.ToString() is { Length: > 0 } text && text is not ("null" or "~") ? text : null;
}
