using Api.Domain;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Actions;

/// <summary>
/// The sentence the user is shown about an execution, and the one line the conversation closes on.
/// </summary>
/// <remarks>
/// <para>
/// One place, because two things now settle an execution: the approval request, when the effect is
/// local and immediate, and the outbox worker, when it is a delivery confirmed minutes later. If
/// each wrote its own copy they would drift, and the sentence describing what happened to somebody's
/// money is not a good place for two vocabularies.
/// </para>
/// <para>
/// The server writes every one of these (ADR 007). No model call narrates an execution: the
/// assistant is not in the loop by the time the answer is known, and a sentence the server wrote
/// cannot be talked into being wrong.
/// </para>
/// </remarks>
public sealed class ActionNarrator(AppDbContext db, IClock clock)
{
    /// <summary>
    /// Five outcomes, five sentences. The copy is the only place a person learns that "approved"
    /// and "done" are not the same thing.
    /// </summary>
    public static string Describe(string summary, ActionExecution execution) => execution switch
    {
        { Status: ActionExecutionStatus.Succeeded } => $"Done: {LowerFirst(summary)}.",

        // Its own sentence, because it is not a refusal, and telling the user "the API said no"
        // would send them looking for a permission problem. What happened is that the thing they
        // were shown moved on, and the only honest answer is to ask again against what it is now.
        { ErrorCode: "resource_changed" } =>
            $"{summary} — not done: the invoice changed after this was proposed, so the approval no "
                + "longer matches what you were shown. Ask again to see the current state.",

        { Status: ActionExecutionStatus.Failed } =>
            $"{summary} — approved, but it did not go through. Nothing changed.",

        { Status: ActionExecutionStatus.Unknown } =>
            $"{summary} — approved. The outcome is not confirmed yet; it is being reconciled.",

        _ => $"{summary} — approved and running.",
    };

    /// <summary>
    /// Appends the outcome to the conversation, so the next turn's history contains what actually
    /// happened rather than a proposal that trails off.
    /// </summary>
    /// <remarks>
    /// Called once per execution, by whoever settles it. An unsettled execution gets no line at all:
    /// an assistant message saying "Done" beside a delivery nobody has confirmed is exactly the
    /// claim this feature exists to stop making.
    /// </remarks>
    public void RecordClosingLine(Guid? conversationId, string message)
    {
        if (conversationId is not { } id)
        {
            return;
        }

        db.Set<Message>().Add(new Message
        {
            ConversationId = id,
            Role = MessageRole.Assistant,
            Content = message,
            CreatedAt = clock.UtcNow,
        });
    }

    /// <summary>
    /// The sentence for an execution reached asynchronously, where the caller has an execution but
    /// not the proposal that phrased it.
    /// </summary>
    public async Task<string> DescribeSettledAsync(ActionExecution execution, CancellationToken cancellationToken)
    {
        var summary = execution.PendingActionId is { } actionId
            ? await db.PendingActions.AsNoTracking()
                .Where(action => action.Id == actionId)
                .Select(action => action.Summary)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        return Describe(summary ?? $"The {execution.ToolName.Replace('_', ' ')} request", execution);
    }

    private static string LowerFirst(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
