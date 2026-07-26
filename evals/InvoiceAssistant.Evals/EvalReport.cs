using System.Text;

namespace InvoiceAssistant.Evals;

/// <summary>Provider, model and prompt identity, recorded so a regression can be attributed.</summary>
public sealed record RunInfo(string Provider, string Model, string PromptHash);

/// <summary>
/// Collects case results and writes the markdown report — pass rate globally and per category —
/// that CI uploads as an artifact and appends to the job summary.
/// </summary>
public sealed class EvalReport
{
    private readonly Lock _lock = new();
    private readonly List<CaseResult> _results = [];

    public RunInfo? Run { get; set; }

    public void Add(CaseResult result)
    {
        lock (_lock)
        {
            _results.Add(result);
        }
    }

    public void Write(string path, long totalTokens, long tokenBudget)
    {
        List<CaseResult> results;
        lock (_lock)
        {
            results = [.. _results.OrderBy(r => r.Category, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)];
        }

        if (results.Count == 0)
        {
            return;
        }

        var passed = results.Count(r => r.Passed);
        var report = new StringBuilder();

        report.AppendLine("# Evals report");
        report.AppendLine();
        report.AppendLine(passed == results.Count
            ? $"**PASS** — {passed}/{results.Count} cases."
            : $"**FAIL** — {passed}/{results.Count} cases passed.");
        report.AppendLine();
        report.AppendLine($"- Date: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
        report.AppendLine($"- Provider: {Run?.Provider} · model: `{Run?.Model}`");
        report.AppendLine($"- Prompt hash: `{Run?.PromptHash}`");
        report.AppendLine($"- Tokens: {totalTokens:N0} of {tokenBudget:N0} budgeted");
        report.AppendLine();

        report.AppendLine("## By category");
        report.AppendLine();
        report.AppendLine("| Category | Passed | Cases |");
        report.AppendLine("|---|---:|---:|");

        foreach (var category in results.GroupBy(r => r.Category))
        {
            report.AppendLine($"| {category.Key} | {category.Count(r => r.Passed)} | {category.Count()} |");
        }

        report.AppendLine();
        report.AppendLine("## Cases");
        report.AppendLine();
        report.AppendLine("| Case | Category | Result | Attempts | Tokens |");
        report.AppendLine("|---|---|---|---:|---:|");

        foreach (var result in results)
        {
            var outcome = result switch
            {
                { Passed: true, Attempts: 1 } => "pass",
                { Passed: true } => "pass (retry)",
                _ => "**fail**",
            };

            report.AppendLine($"| {result.Id} | {result.Category} | {outcome} | {result.Attempts} | {result.TokensUsed:N0} |");
        }

        var failures = results.Where(r => !r.Passed).ToList();
        if (failures.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("## Failures");

            foreach (var failure in failures)
            {
                report.AppendLine();
                report.AppendLine($"### {failure.Id}");
                report.AppendLine();
                report.AppendLine(failure.Failure);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"Evals report written to {path}");
    }
}
