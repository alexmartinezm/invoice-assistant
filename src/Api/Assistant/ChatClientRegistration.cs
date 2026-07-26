using System.ClientModel;
using Api.Assistant.Tools;
using Api.Assistant.Usage;
using Api.Infrastructure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Api.Assistant;

/// <summary>Whether an AI provider is configured, and why not when it is not.</summary>
public sealed record AssistantAvailability(bool IsConfigured, string Reason);

public static class ChatClientRegistration
{
    public static IServiceCollection AddAssistant(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<AssistantOptions>(configuration.GetSection(AssistantOptions.SectionName));
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.Configure<UsageOptions>(configuration.GetSection(UsageOptions.SectionName));
        services.AddScoped<UsageBudget>();

        services.AddSingleton<SystemPromptProvider>();
        services.AddScoped<ReadTools>();
        services.AddScoped<WriteTools>();
        services.AddScoped<ToolCatalog>();
        services.AddScoped<ChatOrchestrator>();

        // Loaded here rather than lazily on first use so a malformed policy file stops the app at
        // startup. A security rule that only fails when someone tries to use it is not a rule.
        var policy = LoadPolicy(configuration, environment);
        services.AddSingleton(policy);
        services.AddScoped<ToolPolicyEngine>();
        services.AddScoped<ToolGate>();
        services.AddScoped<TurnJournal>();

        services.AddTransient<ForwardCallerIdentityHandler>();
        services.AddHttpClient<SelfApiClient>((provider, client) =>
            {
                var baseUrl = provider.GetRequiredService<IOptions<AssistantOptions>>().Value.ApiBaseUrl;
                if (!string.IsNullOrWhiteSpace(baseUrl))
                {
                    client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
                }

                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<ForwardCallerIdentityHandler>();

        var ai = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();

        // A tool that throws unexpectedly reaches the model as a bare "function failed" unless
        // detailed errors are on, which makes a broken tool very hard to debug. On in development,
        // off elsewhere: a stack trace is not something to hand to a language model in production.
        var detailedToolErrors = configuration["ASPNETCORE_ENVIRONMENT"] is "Development";

        services.AddSingleton(DescribeAvailability(ai));
        services.AddAssistantChatPipeline(provider => BuildChatClient(ai, provider), policy, detailedToolErrors);

        return services;
    }

    /// <summary>
    /// The chat pipeline around whatever stands in for the provider — the real client here, a
    /// scripted one in the tests, a recorded one in the evals. Shared so that every consumer runs
    /// the same middleware: the function-invoking loop with its brake, and the usage collector
    /// metering each model call inside that loop.
    /// </summary>
    public static ChatClientBuilder AddAssistantChatPipeline(
        this IServiceCollection services,
        Func<IServiceProvider, IChatClient> providerFactory,
        PolicyDocument policy,
        bool detailedToolErrors) =>
        services.AddChatClient(providerFactory)
            .UseFunctionInvocation(configure: client =>
            {
                // The loop brake, from policies.json like every other limit. It stops the model
                // going round for ever; ToolGate is what refuses an individual call.
                client.MaximumIterationsPerRequest = policy.Limits.MaxToolCallsPerTurn;
                client.IncludeDetailedErrors = detailedToolErrors;
            })
            .Use((inner, provider) => new UsageCollector(
                inner,
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IHttpContextAccessor>(),
                provider.GetRequiredService<IOptions<UsageOptions>>(),
                provider.GetRequiredService<ILogger<UsageCollector>>()));

    private static PolicyDocument LoadPolicy(IConfiguration configuration, IHostEnvironment environment)
    {
        const string relativePath = "policies.json";

        var configured = configuration.GetSection(AssistantOptions.SectionName)[nameof(AssistantOptions.PolicyPath)];
        var path = RepositoryFile.Find(configured, relativePath, environment.ContentRootPath)
            ?? throw new PolicyConfigurationException(
                $"Could not find '{relativePath}'. Set Assistant:PolicyPath to point at it.");

        return PolicyDocument.Load(path);
    }

    private static AssistantAvailability DescribeAvailability(AiOptions ai) => ai.Provider switch
    {
        "azure-openai" when string.IsNullOrWhiteSpace(ai.AzureEndpoint) || string.IsNullOrWhiteSpace(ai.AzureApiKey) =>
            new AssistantAvailability(false, "Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY and AZURE_OPENAI_CHAT_DEPLOYMENT in .env."),
        "azure-openai" when string.IsNullOrWhiteSpace(ai.AzureChatDeployment) =>
            new AssistantAvailability(false, "Set AZURE_OPENAI_CHAT_DEPLOYMENT in .env."),
        "openai-compatible" when string.IsNullOrWhiteSpace(ai.OpenAiApiKey) || string.IsNullOrWhiteSpace(ai.ChatModel) =>
            new AssistantAvailability(false, "Set OPENAI_API_KEY and CHAT_MODEL in .env (OPENAI_BASE_URL is optional)."),
        "azure-openai" or "openai-compatible" => new AssistantAvailability(true, string.Empty),
        _ => new AssistantAvailability(false, $"Unknown AI_PROVIDER '{ai.Provider}'. Use azure-openai or openai-compatible."),
    };

    private static IChatClient BuildChatClient(AiOptions ai, IServiceProvider services)
    {
        var availability = services.GetRequiredService<AssistantAvailability>();
        if (!availability.IsConfigured)
        {
            // The rest of the API stays usable without a key, so the repo can be cloned, started
            // and explored before anyone signs up to a provider.
            return new NotConfiguredChatClient(availability.Reason);
        }

        return ai.Provider is "azure-openai"
            ? new AzureOpenAIClient(new Uri(ai.AzureEndpoint!), new ApiKeyCredential(ai.AzureApiKey!))
                .GetChatClient(ai.AzureChatDeployment!)
                .AsIChatClient()
            : new OpenAIClient(
                    new ApiKeyCredential(ai.OpenAiApiKey!),
                    new OpenAIClientOptions { Endpoint = ResolveEndpoint(ai.OpenAiBaseUrl) })
                .GetChatClient(ai.ChatModel!)
                .AsIChatClient();
    }

    private static Uri? ResolveEndpoint(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : new Uri(baseUrl, UriKind.Absolute);
}

/// <summary>Stands in for a real client when no provider is configured, and says so clearly.</summary>
internal sealed class NotConfiguredChatClient(string reason) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new AssistantNotConfiguredException(reason);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new AssistantNotConfiguredException(reason);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
