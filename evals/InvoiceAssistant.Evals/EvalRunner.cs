using System.Net.Http.Json;
using System.Text.Json;
using Api.Assistant.Tools;
using Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceAssistant.Evals;

public sealed record CaseResult(
    string Id,
    string Category,
    bool Passed,
    int Attempts,
    long TokensUsed,
    string? Failure);

/// <summary>
/// Runs one case: resolve placeholders, snapshot the database, run the turn over the real
/// <c>POST /api/chat</c>, diff, and check every expectation. Each case gets one retry — a model at
/// temperature 0 still has residual variance, and an isolated flake should not sink a PR while two
/// failures in a row still do.
/// </summary>
public sealed class EvalRunner(EvalHost host)
{
    private static readonly HashSet<string> WriteToolNames =
        [.. WriteToolCatalog.All.Select(tool => tool.Name)];

    private readonly Placeholders _placeholders = new(host);

    public async Task<CaseResult> RunAsync(EvalCase evalCase)
    {
        var budget = EvalEnvironment.Current.RunTokenBudget;
        var tokensAtStart = host.Recorder.TotalTokens;
        string? failure = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            // The budget check failing the run is deliberate: see EvalEnvironment.RunTokenBudget.
            if (host.Recorder.TotalTokens > budget)
            {
                return new CaseResult(
                    evalCase.Id,
                    evalCase.Category,
                    Passed: false,
                    attempt - 1,
                    host.Recorder.TotalTokens - tokensAtStart,
                    $"Run token budget exceeded: {host.Recorder.TotalTokens} tokens used, budget is {budget}. "
                    + "Raise EVALS_RUN_TOKEN_BUDGET only if the extra spend is justified.");
            }

            failure = await RunAttemptAsync(evalCase);

            if (failure is null)
            {
                return new CaseResult(
                    evalCase.Id,
                    evalCase.Category,
                    Passed: true,
                    attempt,
                    host.Recorder.TotalTokens - tokensAtStart,
                    Failure: null);
            }
        }

        return new CaseResult(
            evalCase.Id,
            evalCase.Category,
            Passed: false,
            Attempts: 2,
            host.Recorder.TotalTokens - tokensAtStart,
            failure);
    }

    /// <summary>Returns null when every expectation holds, otherwise what failed and what happened.</summary>
    private async Task<string?> RunAttemptAsync(EvalCase evalCase)
    {
        // Placeholders resolve (and fixture invoices are created) before the snapshot, so nothing
        // the harness itself does is ever counted as a write of the model's.
        _placeholders.BeginAttempt();
        var prompt = await _placeholders.ApplyAsync(evalCase.Prompt);

        DbSnapshot before;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            before = await DbSnapshot.CaptureAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        host.Recorder.BeginTurn();
        host.ServerLogs.Clear();

        using var client = await host.ClientForAsync(evalCase.User);
        using var response = await client.PostAsJsonAsync("/api/chat", new { message = prompt });

        if (!response.IsSuccessStatusCode)
        {
            return $"POST /api/chat answered {(int)response.StatusCode}.";
        }

        var events = await ServerSentEvents.ReadAllAsync(response);
        if (events.FirstOrDefault(e => e.Name == "error") is { } error)
        {
            // The SSE payload hides the reason on purpose; the harness's log capture has it.
            return host.ServerLogs.Latest is { } serverLog
                ? $"The turn failed: {error.Data}\nServer log: {serverLog}"
                : $"The turn failed: {error.Data}";
        }

        TurnFacts facts;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            facts = await before.DiffAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        return await CheckAsync(evalCase.Expect, host.Recorder.Calls, facts);
    }

    private async Task<string?> CheckAsync(
        EvalExpectation expect,
        IReadOnlyList<RecordedToolCall> calls,
        TurnFacts facts)
    {
        List<string> failures = [];

        if (expect.AnyOf.Count > 0 && !await MatchesAnyAsync(expect.AnyOf, calls))
        {
            failures.Add("none of the accepted tool calls was made");
        }

        if (expect.NoTools && calls.Count > 0)
        {
            failures.Add("expected no tool calls at all");
        }

        if (expect.NoWriteTools && calls.Any(call => WriteToolNames.Contains(call.Name)))
        {
            failures.Add("expected no write tool calls");
        }

        if (expect.WritesExecuted is { } writes && facts.WritesExecuted != writes)
        {
            failures.Add($"expected {writes} executed write(s), the database shows {facts.WritesExecuted}");
        }

        if (expect.PendingActionSpecified)
        {
            switch (expect.PendingActionTool)
            {
                case null when facts.NewPendingActionTools.Count > 0:
                    failures.Add("expected no pending action to be created");
                    break;
                case { } tool when !facts.NewPendingActionTools.Contains(tool):
                    failures.Add($"expected a pending action for {tool}");
                    break;
            }
        }

        failures.AddRange(expect.AuditContains
            .Where(decision => !facts.NewAuditDecisions.Contains(decision))
            .Select(decision => $"expected an audit event with decision '{decision}'"));

        failures.AddRange(expect.AuditNotContains
            .Where(facts.NewAuditDecisions.Contains)
            .Select(decision => $"expected no audit event with decision '{decision}'"));

        return failures.Count == 0
            ? null
            : $"{string.Join("; ", failures)}.\n{Describe(calls, facts)}";
    }

    private async Task<bool> MatchesAnyAsync(
        IReadOnlyList<ToolExpectation> alternatives,
        IReadOnlyList<RecordedToolCall> calls)
    {
        foreach (var alternative in alternatives)
        {
            foreach (var call in calls.Where(c => c.Name == alternative.Tool))
            {
                if (await ArgsMatchAsync(alternative.ArgsMatch, call.Args))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<bool> ArgsMatchAsync(IReadOnlyDictionary<string, string?> expected, JsonElement args)
    {
        foreach (var (key, raw) in expected)
        {
            if (args.ValueKind is not JsonValueKind.Object || !args.TryGetProperty(key, out var actual))
            {
                return false;
            }

            var value = await _placeholders.ResolveExpectedValueAsync(raw);

            var matches = actual.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False =>
                    bool.TryParse(value, out var flag) && flag == actual.GetBoolean(),
                JsonValueKind.Number =>
                    decimal.TryParse(value, out var number) && number == actual.GetDecimal(),
                JsonValueKind.String =>
                    string.Equals(actual.GetString(), value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(IReadOnlyList<RecordedToolCall> calls, TurnFacts facts)
    {
        var toolCalls = calls.Count == 0
            ? "(none)"
            : string.Join(", ", calls.Select(call => $"{call.Name}({call.Args})"));

        return $"Tool calls: {toolCalls}. Writes: {facts.WritesExecuted}. "
            + $"Pending actions: [{string.Join(", ", facts.NewPendingActionTools)}]. "
            + $"Audit decisions: [{string.Join(", ", facts.NewAuditDecisions)}].";
    }
}
