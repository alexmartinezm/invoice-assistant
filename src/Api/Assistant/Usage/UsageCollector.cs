using System.Diagnostics;
using System.Runtime.CompilerServices;
using Api.Domain;
using Api.Features.Auth;
using Api.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Api.Assistant.Usage;

/// <summary>
/// Chat-client middleware that puts a price on every model call. It sits between the
/// function-invoking client and the provider, so a turn that loops over tools is metered once per
/// call — which is also where the daily kill switch has to live: checked before each call, not
/// once per turn, or a single long turn could sail past the cap.
/// </summary>
/// <remarks>
/// The collector is registered as part of the chat pipeline (see
/// <see cref="ChatClientRegistration.AddAssistantChatPipeline"/>), so the scripted tests and the
/// evals meter their calls with the same code that meters production.
/// </remarks>
public sealed class UsageCollector(
    IChatClient innerClient,
    IServiceScopeFactory scopes,
    IHttpContextAccessor httpContextAccessor,
    IOptions<UsageOptions> usageOptions,
    ILogger<UsageCollector> logger) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureBudgetRemainsAsync(cancellationToken);

        using var activity = AssistantTelemetry.Source.StartActivity("assistant.model_call");
        var stopwatch = Stopwatch.StartNew();

        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        stopwatch.Stop();
        var toolCalls = response.Messages
            .SelectMany(message => message.Contents)
            .Count(content => content is FunctionCallContent);
        await RecordAsync(response.ModelId ?? options?.ModelId, response.Usage, toolCalls, stopwatch.Elapsed, activity);

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureBudgetRemainsAsync(cancellationToken);

        using var activity = AssistantTelemetry.Source.StartActivity("assistant.model_call");
        var stopwatch = Stopwatch.StartNew();

        string? modelId = null;
        UsageDetails? usage = null;
        var toolCalls = 0;
        var sawAnyUpdate = false;

        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                sawAnyUpdate = true;
                modelId ??= update.ModelId;

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case UsageContent reported:
                            usage = Merge(usage, reported.Details);
                            break;
                        case FunctionCallContent:
                            toolCalls++;
                            break;
                    }
                }

                yield return update;
            }
        }
        finally
        {
            // Runs on completion and on early disposal alike: a user who closes the tab mid-answer
            // still consumed the tokens, so the call is still recorded.
            stopwatch.Stop();

            if (sawAnyUpdate)
            {
                await RecordAsync(modelId ?? options?.ModelId, usage, toolCalls, stopwatch.Elapsed, activity);
            }
        }
    }

    /// <summary>Sums usage across updates: providers may report it in more than one chunk.</summary>
    private static UsageDetails Merge(UsageDetails? total, UsageDetails next)
    {
        if (total is null)
        {
            return next;
        }

        return new UsageDetails
        {
            InputTokenCount = (total.InputTokenCount ?? 0) + (next.InputTokenCount ?? 0),
            OutputTokenCount = (total.OutputTokenCount ?? 0) + (next.OutputTokenCount ?? 0),
            TotalTokenCount = (total.TotalTokenCount ?? 0) + (next.TotalTokenCount ?? 0),
        };
    }

    private async Task EnsureBudgetRemainsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<UsageBudget>()
            .GetStatusAsync(cancellationToken);

        if (status.Exhausted)
        {
            AssistantTelemetry.BudgetRejections.Add(1);
            logger.LogWarning(
                "Daily budget exhausted: {Spent}€ of {Budget}€ — model call refused",
                status.SpentTodayEur, status.DailyBudgetEur);
            throw new DailyBudgetExhaustedException(status);
        }
    }

    private async Task RecordAsync(
        string? modelId,
        UsageDetails? usage,
        int toolCalls,
        TimeSpan elapsed,
        Activity? activity)
    {
        var http = httpContextAccessor.HttpContext;
        if (http?.User.Identity?.IsAuthenticated is not true)
        {
            logger.LogWarning("A model call ran outside an authenticated request; usage was not recorded");
            return;
        }

        var model = modelId ?? "unknown";
        var promptTokens = (int)(usage?.InputTokenCount ?? 0);
        var completionTokens = (int)(usage?.OutputTokenCount ?? 0);

        var price = usageOptions.Value.PriceFor(model);
        var cost = price?.CostFor(promptTokens, completionTokens) ?? 0m;

        if (price is null)
        {
            // Zero-cost rows keep the ledger honest about what happened, but they also mean the
            // kill switch cannot see this spend — hence the loud warning and its own metric.
            AssistantTelemetry.UnpricedModelCalls.Add(1);
            logger.LogWarning("No price configured for model {Model}; its cost is recorded as zero", model);
        }

        activity?.SetTag("assistant.model", model);
        activity?.SetTag("assistant.prompt_tokens", promptTokens);
        activity?.SetTag("assistant.completion_tokens", completionTokens);
        activity?.SetTag("assistant.tool_calls", toolCalls);
        activity?.SetTag("assistant.cost_eur", (double)cost);

        AssistantTelemetry.ModelCalls.Add(1);
        AssistantTelemetry.Tokens.Add(promptTokens, new KeyValuePair<string, object?>("direction", "input"));
        AssistantTelemetry.Tokens.Add(completionTokens, new KeyValuePair<string, object?>("direction", "output"));
        AssistantTelemetry.CostEur.Add((double)cost);

        // Its own scope, not the request's: the request's DbContext may be mid-operation in the
        // orchestrator, and a metering write should never fail a turn by tripping over it.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        db.UsageRecords.Add(new UsageRecord
        {
            ConversationId = http.RequestServices.GetService<TurnJournal>()?.ConversationId,
            UserId = http.User.Id(),
            Model = model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ToolCallCount = toolCalls,
            CostEur = cost,
            LatencyMs = (int)elapsed.TotalMilliseconds,
            CreatedAt = clock.UtcNow,
        });

        // Not the request token: a cancelled turn still has to pay for what it used.
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
