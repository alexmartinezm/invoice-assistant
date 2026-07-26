namespace Api.Domain;

public sealed class Conversation
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid UserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Hash of <c>prompts/system.md</c> as it was when the conversation started. Recorded so a
    /// prompt regression can be correlated with the conversations that ran under it.
    /// </summary>
    public required string SystemPromptHash { get; init; }

    public List<Message> Messages { get; } = [];
}
