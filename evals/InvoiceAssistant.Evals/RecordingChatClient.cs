using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace InvoiceAssistant.Evals;

/// <summary>A tool call the model actually made, with its complete arguments.</summary>
public sealed record RecordedToolCall(string CallId, string Name, JsonElement Args);

/// <summary>
/// Sits between the function-invoking middleware and the real provider, so the harness can see
/// what the free-form transcript never states reliably: which tools the model called, with which
/// arguments, and how many tokens the round trips cost.
/// </summary>
/// <remarks>
/// Calls are captured from two places and de-duplicated by call id: the streamed response (where
/// providers emit each <see cref="FunctionCallContent"/> once its arguments are complete) and the
/// request messages of the follow-up round trip (which carry the executed calls in full). Either
/// alone has a gap; together they see every call even when a provider streams oddly or the
/// iteration cap cuts the loop short.
/// </remarks>
public sealed class RecordingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _lock = new();
    private readonly List<RecordedToolCall> _calls = [];
    private readonly HashSet<string> _seenCallIds = [];

    public long TotalTokens { get; private set; }

    public int ModelCalls { get; private set; }

    public IReadOnlyList<RecordedToolCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Forgets the previous case's calls. Token totals run for the whole suite on purpose.</summary>
    public void BeginTurn()
    {
        lock (_lock)
        {
            _calls.Clear();
            _seenCallIds.Clear();
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObserveRequest(messages);

        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        lock (_lock)
        {
            ModelCalls++;
            CountUsage(response.Usage);

            foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
            {
                Record(call);
            }
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObserveRequest(messages);

        lock (_lock)
        {
            ModelCalls++;
        }

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            lock (_lock)
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent call:
                            Record(call);
                            break;
                        case UsageContent usage:
                            CountUsage(usage.Details);
                            break;
                    }
                }
            }

            yield return update;
        }
    }

    /// <summary>Picks completed tool calls out of the conversation the middleware sends back in.</summary>
    private void ObserveRequest(IEnumerable<ChatMessage> messages)
    {
        lock (_lock)
        {
            foreach (var call in messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
            {
                Record(call);
            }
        }
    }

    private void Record(FunctionCallContent call)
    {
        if (!_seenCallIds.Add(call.CallId))
        {
            return;
        }

        var args = JsonSerializer.SerializeToElement(
            call.Arguments ?? new Dictionary<string, object?>(),
            Json);

        _calls.Add(new RecordedToolCall(call.CallId, call.Name, args));
    }

    private void CountUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        TotalTokens += (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0);

        if (usage.InputTokenCount is null && usage.OutputTokenCount is null)
        {
            TotalTokens += usage.TotalTokenCount ?? 0;
        }
    }
}
