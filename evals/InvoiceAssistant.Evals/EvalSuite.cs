using Api.Assistant;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceAssistant.Evals;

/// <summary>
/// Shared state for the whole run: one application host (and one throwaway database) for every
/// case, the report, and the lazy boot that keeps an unconfigured run — a fork's PR, a plain
/// <c>dotnet test</c> without credentials — from ever touching PostgreSQL or a provider.
/// </summary>
public sealed class EvalFixture : IDisposable
{
    private readonly Lazy<EvalHost> _host = new(() => new EvalHost());
    private readonly Lazy<EvalRunner> _runner;
    private readonly EvalReport _report = new();

    public EvalFixture()
    {
        _runner = new Lazy<EvalRunner>(() => new EvalRunner(_host.Value));
    }

    public async Task<CaseResult> RunAsync(string caseId)
    {
        var evalCase = EvalCases.All.TryGetValue(caseId, out var found)
            ? found
            : throw new InvalidOperationException($"No case with id '{caseId}'.");

        _report.Run ??= new RunInfo(
            EvalEnvironment.Current.Provider,
            EvalEnvironment.Current.Model,
            _host.Value.Services.GetRequiredService<SystemPromptProvider>().Hash);

        var result = await _runner.Value.RunAsync(evalCase);
        _report.Add(result);
        return result;
    }

    public void Dispose()
    {
        if (_host.IsValueCreated)
        {
            _report.Write(
                EvalEnvironment.Current.ReportPath,
                _host.Value.Recorder.TotalTokens,
                EvalEnvironment.Current.RunTokenBudget);

            _host.Value.Dispose();
        }
    }
}

[CollectionDefinition("evals")]
public sealed class EvalCollection : ICollectionFixture<EvalFixture>;

/// <summary>
/// One test per YAML case. A red case is a prompt (or policy, or tool schema) regression: fix the
/// change, never the case — or justify the case change in the PR (see .agent/delivery.md).
/// </summary>
[Collection("evals")]
public sealed class EvalSuite(EvalFixture fixture)
{
    public static TheoryData<string> CaseIds
    {
        get
        {
            var ids = new TheoryData<string>();
            foreach (var id in EvalCases.All.Keys.OrderBy(id => id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }

            return ids;
        }
    }

    [SkippableTheory]
    [MemberData(nameof(CaseIds))]
    public async Task Case(string id)
    {
        Skip.If(
            !EvalEnvironment.Current.IsConfigured,
            $"Evals skipped: {EvalEnvironment.Current.SkipReason}");

        var result = await fixture.RunAsync(id);

        Assert.True(result.Passed, result.Failure);
    }
}
