namespace Api.Assistant;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>
    /// Base URL the assistant's tools call. Empty means "the URL this request arrived on", which
    /// is what you want in dev and in a single-container deployment.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Path to <c>prompts/system.md</c>. Empty means "find it by walking up from the app".</summary>
    public string? SystemPromptPath { get; set; }

    /// <summary>How many past messages are replayed to the model. Bounds cost and context window.</summary>
    public int HistoryMessages { get; set; } = 20;

    public int MaxInputCharacters { get; set; } = 2_000;

    public int MaxOutputTokens { get; set; } = 800;

    /// <summary>
    /// Loop brake: a model that keeps chaining reads without converging gets cut off. In F2 this
    /// limit moves into <c>policies.json</c> together with the write limits.
    /// </summary>
    public int MaxToolCallsPerTurn { get; set; } = 8;

    /// <summary>Chat turns allowed per user per minute. Every turn costs money.</summary>
    public int TurnsPerMinute { get; set; } = 20;
}

public sealed class AiOptions
{
    public const string SectionName = "AI";

    /// <summary><c>azure-openai</c> or <c>openai-compatible</c>.</summary>
    public string Provider { get; set; } = "azure-openai";

    public string? AzureEndpoint { get; set; }

    public string? AzureApiKey { get; set; }

    public string? AzureChatDeployment { get; set; }

    public string? OpenAiBaseUrl { get; set; }

    public string? OpenAiApiKey { get; set; }

    public string? ChatModel { get; set; }
}

/// <summary>
/// Thrown when the app runs without AI credentials. The rest of the API still works, so the repo
/// can be cloned, started and explored before anyone pastes a key in.
/// </summary>
public sealed class AssistantNotConfiguredException(string message) : Exception(message);
