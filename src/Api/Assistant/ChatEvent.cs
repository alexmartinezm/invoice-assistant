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
[JsonDerivedType(typeof(TurnCompleted), "done")]
[JsonDerivedType(typeof(TurnFailed), "error")]
public abstract record ChatEvent
{
    [JsonIgnore]
    public abstract string EventName { get; }
}

/// <summary>Sent first, so the client can pin the conversation and show the trace id.</summary>
public sealed record ConversationStarted(Guid ConversationId, string TraceId) : ChatEvent
{
    public override string EventName => "conversation";
}

/// <summary>A tool call starting or finishing: this is what drives "checking invoices…" in the UI.</summary>
public sealed record ToolActivity(string Tool, string Phase, string Label) : ChatEvent
{
    public override string EventName => "activity";
}

public sealed record TextToken(string Text) : ChatEvent
{
    public override string EventName => "token";
}

public sealed record TurnCompleted(Guid ConversationId, string TraceId) : ChatEvent
{
    public override string EventName => "done";
}

public sealed record TurnFailed(string Message) : ChatEvent
{
    public override string EventName => "error";
}
