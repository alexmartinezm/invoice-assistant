using Api.Domain;

namespace Api.Features.Actions;

/// <summary>
/// An execution as a client is allowed to see it.
/// </summary>
/// <remarks>
/// It carries the status, the attempt count and a machine-readable error code — not the stored
/// response body, and not the raw error detail. What a caller needs is "where has this got to";
/// what the body would add is a second copy of a payload they already received, with whatever a
/// failing endpoint happened to put in it.
/// </remarks>
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
    string? ErrorDetail,
    Guid? DeliveryId,
    string? DeliveryStatus)
{
    public static ActionExecutionView Of(ActionExecution execution, string? deliveryStatus = null) => new(
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
        execution.ErrorDetail,
        execution.DeliveryId,
        deliveryStatus);
}
