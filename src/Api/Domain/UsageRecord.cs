namespace Api.Domain;

/// <summary>
/// One call to the model: which model, how many tokens each way, what that cost and how long it
/// took. A turn that uses tools makes several of these; summing them per conversation is what
/// puts a price on "the assistant answered a question".
/// </summary>
/// <remarks>
/// The cost is computed here, in euros, at write time — from the prices in configuration, never by
/// the model. The daily spend kill switch sums this table, so a row is written for every call even
/// when the provider reports no usage.
/// </remarks>
public sealed class UsageRecord
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The conversation the call served. Null only if a call ran outside a chat turn.</summary>
    public Guid? ConversationId { get; init; }

    public Guid UserId { get; init; }

    public required string Model { get; init; }

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    /// <summary>Tool calls the model requested in this response — not necessarily executed ones.</summary>
    public int ToolCallCount { get; init; }

    public decimal CostEur { get; init; }

    public int LatencyMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
