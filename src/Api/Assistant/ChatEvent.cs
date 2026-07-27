using System.Text.Json.Serialization;

namespace Api.Assistant;

/// <summary>
/// What a turn emits over SSE. The <c>type</c> discriminator in the payload matches the SSE event
/// name, so a client can switch on either one.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConversationStarted), "conversation")]
[JsonDerivedType(typeof(ToolActivity), "activity")]
[JsonDerivedType(typeof(TextToken), "token")]
[JsonDerivedType(typeof(ApprovalRequired), "approval_required")]
[JsonDerivedType(typeof(ToolBlocked), "blocked")]
[JsonDerivedType(typeof(TurnCompleted), "done")]
[JsonDerivedType(typeof(TurnFailed), "error")]
public abstract record ChatEvent
{
    /// <summary>
    /// The SSE event name. A method rather than a property on purpose: a property here is
    /// serialized into every payload unless each override remembers <c>[JsonIgnore]</c>, and the
    /// one that forgets ships a redundant field on the wire. This cannot be forgotten.
    /// </summary>
    public abstract string EventName();
}

/// <summary>Sent first, so the client can pin the conversation and show the trace id.</summary>
public sealed record ConversationStarted(Guid ConversationId, string TraceId) : ChatEvent
{
    public override string EventName() => "conversation";
}

/// <summary>A tool call starting or finishing: this is what drives "checking invoices…" in the UI.</summary>
public sealed record ToolActivity(string Tool, string Phase, string Label) : ChatEvent
{
    public override string EventName() => "activity";
}

public sealed record TextToken(string Text) : ChatEvent
{
    public override string EventName() => "token";
}

/// <summary>
/// A write the assistant proposed and cannot perform on its own. Carries everything the approval
/// card needs, including the deadline — an approval the user cannot see expiring is a trap.
/// </summary>
/// <param name="CanApprove">
/// Whether this user clears the tool's role floor. Sent with the proposal so the card knows on
/// arrival whether to offer an Approve button, rather than finding out from a 403 after the click.
/// </param>
/// <param name="RequiredRole">The role the tool needs, so the card can name who to escalate to.</param>
public sealed record ApprovalRequired(
    Guid ActionId,
    string Tool,
    string Summary,
    DateTimeOffset ExpiresAt,
    bool CanApprove,
    string RequiredRole) : ChatEvent
{
    public override string EventName() => "approval_required";
}

/// <summary>
/// A call the gate refused outright — denied by policy, or over a per-turn limit. Separate from
/// <see cref="TurnFailed"/> because nothing went wrong: the system did exactly its job.
/// </summary>
public sealed record ToolBlocked(string Tool, string Reason) : ChatEvent
{
    public override string EventName() => "blocked";
}

public sealed record TurnCompleted(Guid ConversationId, string TraceId) : ChatEvent
{
    public override string EventName() => "done";
}

public sealed record TurnFailed(string Message) : ChatEvent
{
    public override string EventName() => "error";
}
