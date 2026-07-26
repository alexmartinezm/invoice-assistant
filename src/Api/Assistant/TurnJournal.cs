namespace Api.Assistant;

/// <summary>
/// Per-turn state, scoped to the request.
/// </summary>
/// <remarks>
/// The tools run inside <c>Microsoft.Extensions.AI</c>'s function-invoking middleware, so
/// <see cref="ChatOrchestrator"/> never sees them execute. This is how what happened in there gets
/// back out: the gate counts calls and records events here, and the orchestrator drains them
/// between streaming updates to emit the SSE the UI needs.
/// </remarks>
public sealed class TurnJournal
{
    private readonly Queue<ChatEvent> _events = new();

    public int ToolCalls { get; private set; }

    /// <summary>Writes that actually reached the database this turn.</summary>
    public int WritesExecuted { get; private set; }

    public Guid? ConversationId { get; set; }

    /// <summary>True once a proposal is waiting, so the UI can keep the composer honest.</summary>
    public bool HasPendingApproval { get; private set; }

    public void CountToolCall() => ToolCalls++;

    public void CountWrite() => WritesExecuted++;

    public void Record(ChatEvent chatEvent)
    {
        if (chatEvent is ApprovalRequired)
        {
            HasPendingApproval = true;
        }

        _events.Enqueue(chatEvent);
    }

    /// <summary>Hands over everything recorded since the last drain, leaving the journal empty.</summary>
    public IEnumerable<ChatEvent> Drain()
    {
        while (_events.TryDequeue(out var chatEvent))
        {
            yield return chatEvent;
        }
    }
}
