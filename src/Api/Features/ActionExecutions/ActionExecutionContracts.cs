using Api.Domain;

namespace Api.Features.Actions;

/// <summary>
/// An execution as a client is allowed to see it.
/// </summary>
/// <remarks>
/// It carries the status, the attempt count and a machine-readable error code — not the stored
/// response body, and not the raw error detail. What a caller needs is "where has this got to";
/// what the body would add is a second copy of a payload they already received, with whatever a
/// failing endpoint happened to put in it. <see cref="ErrorCode"/> is a closed, server-chosen
/// vocabulary (<c>transport_lost</c>, <c>delivery_rejected</c>, ...); the execution's own
/// <c>ErrorDetail</c> is not — it can hold a caught exception's <c>Message</c> or a provider's own
/// error text — and does not appear here at all, never mind truncated, for exactly that reason.
/// </remarks>
/// <param name="Message">
/// The sentence to show. The server writes every one of these (ADR 007), and this field is how a
/// polling client gets the same one the transcript did. A client that rebuilds the sentence from
/// <see cref="Status"/> will eventually contradict the server — it did, for the delivery the
/// provider refused, where the status is <c>failed</c> but the invoice was issued all the same.
/// </param>
public sealed record ActionExecutionView(
    Guid ExecutionId,
    Guid? ActionId,
    string Tool,
    string Decision,
    string Status,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    Guid? DeliveryId,
    string? DeliveryStatus,
    string? Message)
{
    public static ActionExecutionView Of(
        ActionExecution execution,
        string? deliveryStatus = null,
        string? message = null) => new(
        execution.Id,
        execution.PendingActionId,
        execution.ToolName,
        execution.Decision.ToString().ToLowerInvariant(),
        execution.Status.ToString().ToLowerInvariant(),
        execution.AttemptCount,
        execution.CreatedAt,
        execution.StartedAt,
        execution.CompletedAt,
        execution.ErrorCode,
        execution.DeliveryId,
        deliveryStatus,
        message);
}
