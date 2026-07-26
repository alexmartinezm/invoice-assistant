using Api.Infrastructure;

namespace InvoiceAssistant.Evals;

/// <summary>
/// How this run is configured, resolved once from environment variables. The evals call a real
/// model on purpose — that is the whole point of the suite — so everything here is about doing
/// that safely: a pinned cheap model, a hard token budget, and a clean skip when no key is set
/// (forks never receive secrets, and a fork's PR must not fail for that).
/// </summary>
public sealed class EvalEnvironment
{
    public static EvalEnvironment Current { get; } = Resolve();

    /// <summary>Null when the run can proceed; otherwise the reason every case is skipped.</summary>
    public string? SkipReason { get; private init; }

    public bool IsConfigured => SkipReason is null;

    /// <summary><c>azure-openai</c> or <c>openai-compatible</c>, decided by which credentials are set.</summary>
    public string Provider { get; private init; } = string.Empty;

    /// <summary>The eval model — <c>EVALS_MODEL</c>, deliberately separate from the demo's chat model.</summary>
    public string Model { get; private init; } = string.Empty;

    public string? AzureEndpoint { get; private init; }

    public string? AzureApiKey { get; private init; }

    public string? OpenAiBaseUrl { get; private init; }

    public string? OpenAiApiKey { get; private init; }

    /// <summary>
    /// Total tokens (input + output, as reported by the provider) the whole run may spend. The
    /// budget failing the run is a feature: a prompt change that doubles token use is a regression
    /// even when every case still passes.
    /// </summary>
    public long RunTokenBudget { get; private init; }

    /// <summary>Where the markdown report is written. CI uploads it and appends it to the job summary.</summary>
    public string ReportPath { get; private init; } = string.Empty;

    public string RepositoryRoot { get; } = FindRepositoryRoot();

    public string CasesDirectory => Path.Combine(RepositoryRoot, "evals", "cases");

    private static EvalEnvironment Resolve()
    {
        var model = Variable("EVALS_MODEL");
        var azureEndpoint = Variable("AZURE_OPENAI_ENDPOINT");
        var azureApiKey = Variable("AZURE_OPENAI_API_KEY");
        var openAiApiKey = Variable("OPENAI_API_KEY");

        var provider = azureEndpoint is not null && azureApiKey is not null
            ? "azure-openai"
            : openAiApiKey is not null
                ? "openai-compatible"
                : null;

        var skipReason = (model, provider) switch
        {
            (null, _) => "EVALS_MODEL is not set.",
            (_, null) => "No provider credentials: set AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY, or OPENAI_API_KEY.",
            _ => null,
        };

        return new EvalEnvironment
        {
            SkipReason = skipReason,
            Provider = provider ?? string.Empty,
            Model = model ?? string.Empty,
            AzureEndpoint = azureEndpoint,
            AzureApiKey = azureApiKey,
            OpenAiBaseUrl = Variable("OPENAI_BASE_URL"),
            OpenAiApiKey = openAiApiKey,
            RunTokenBudget = long.TryParse(Variable("EVALS_RUN_TOKEN_BUDGET"), out var budget) ? budget : 250_000,
            ReportPath = Variable("EVALS_REPORT_PATH") ?? Path.Combine(AppContext.BaseDirectory, "evals-report.md"),
        };
    }

    private static string? Variable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static string FindRepositoryRoot()
    {
        var marker = RepositoryFile.Find(configured: null, "policies.json", AppContext.BaseDirectory)
            ?? throw new FileNotFoundException("Could not find policies.json from the eval binaries.");

        return Path.GetDirectoryName(marker)!;
    }
}
