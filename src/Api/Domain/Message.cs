namespace Api.Domain;

public enum MessageRole
{
    User,
    Assistant,
}

public sealed class Message
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ConversationId { get; init; }

    public required MessageRole Role { get; init; }

    public required string Content { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
