using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Api.Tests.Support;

/// <summary>
/// A model whose answers are written in advance. The normal test suite never calls a real
/// provider: tests have to be free, offline and deterministic. Checks against a real model are
/// the evals job (F3), which runs separately and on a budget.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    public const string ScriptedModelId = "scripted-model";

    /// <summary>Fixed usage per round trip, so cost assertions are arithmetic, not tolerance.</summary>
    public const int PromptTokensPerCall = 120;

    public const int CompletionTokensPerCall = 45;

    private readonly Queue<IReadOnlyList<AIContent>> _turns = new();

    /// <summary>Everything the model was shown, so tests can assert on prompt and history.</summary>
    public List<ChatMessage> ObservedMessages { get; } = [];

    /// <summary>The tools offered on the last call: this is the capability boundary under test.</summary>
    public IList<AITool> OfferedTools { get; private set; } = [];

    /// <summary>
    /// Queues what the model will "say", one entry per round trip. A round trip that contains a
    /// <see cref="FunctionCallContent"/> makes the pipeline run the tool and come back for more.
    /// </summary>
    public void Script(params IReadOnlyList<AIContent>[] turns)
    {
        _turns.Clear();
        ObservedMessages.Clear();
        OfferedTools = [];

        foreach (var turn in turns)
        {
            _turns.Enqueue(turn);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> reply = [];

        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            reply.Add(new ChatMessage(ChatRole.Assistant, [.. update.Contents]));
        }

        return new ChatResponse(reply);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObservedMessages.AddRange(messages);
        OfferedTools = options?.Tools ?? [];

        IReadOnlyList<AIContent> contents = _turns.Count > 0
            ? _turns.Dequeue()
            : [new TextContent("(the script ran out of turns)")];

        var messageId = Guid.NewGuid().ToString("N");

        foreach (var content in contents)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [content])
            {
                MessageId = messageId,
                ModelId = ScriptedModelId,
            };
        }

        // Usage arrives as the stream's final chunk, the way real providers report it, so the
        // usage collector is exercised on the same shape it sees in production.
        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [new UsageContent(new UsageDetails
            {
                InputTokenCount = PromptTokensPerCall,
                OutputTokenCount = CompletionTokensPerCall,
                TotalTokenCount = PromptTokensPerCall + CompletionTokensPerCall,
            })])
        {
            MessageId = messageId,
            ModelId = ScriptedModelId,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
