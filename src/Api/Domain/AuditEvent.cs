namespace Api.Domain;

/// <summary>
/// How a tool call was resolved. This vocabulary is the ground truth the eval suite asserts
/// against — never the assistant's prose, which can claim anything.
/// </summary>
public enum AuditDecision
{
    /// <summary>Policy said <c>allow</c>; executed without asking anyone.</summary>
    Auto,

    /// <summary>A human approved a <c>PendingAction</c> and it then executed.</summary>
    Confirmed,

    /// <summary>Policy said <c>deny</c>; nothing executed.</summary>
    Denied,

    /// <summary>A per-turn or per-conversation limit stopped it; nothing executed.</summary>
    Blocked,
}

/// <summary>
/// The record of what the assistant was allowed to do, and what it was not.
/// </summary>
/// <remarks>
/// Written for every write attempt, including the ones that never touched the database. A gate
/// that only logs its successes cannot prove it blocked anything, and "the injection executed no
/// writes" is exactly the claim this repo has to be able to demonstrate.
/// </remarks>
public sealed class AuditEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public Guid UserId { get; init; }

    /// <summary>What was attempted, e.g. <c>tool_call</c> or <c>action_approved</c>.</summary>
    public required string Action { get; init; }

    public string? ToolName { get; init; }

    /// <summary>The arguments as the model supplied them, plus the reason for the decision.</summary>
    public string PayloadJson { get; init; } = "{}";

    public required AuditDecision Decision { get; init; }

    public Guid? ConversationId { get; init; }
}
