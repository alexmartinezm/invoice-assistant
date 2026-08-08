using System.Diagnostics;
using System.Text.Json;
using Api.Domain;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Assistant.Tools;

/// <summary>
/// What one attempt produced: the execution as it now stands, and the JSON the caller — the model,
/// or the approval endpoint — is handed.
/// </summary>
public sealed record ExecutionAttempt(ActionExecution Execution, JsonElement Payload);

/// <summary>
/// Performs one attempt of an <see cref="ActionExecution"/> against our own API, and records what
/// became of it before the caller is told anything.
/// </summary>
/// <remarks>
/// <para>
/// The attempt is claimed with a conditional <c>UPDATE</c>, for the same reason a proposal is
/// (ADR 009). Twenty tabs clicking approve all arrive here holding the same execution; exactly one
/// of them may put a request on the wire, and the other nineteen are handed the state instead. An
/// in-memory status check would let two of them through, and two requests carrying one idempotency
/// key is a race against the receipt rather than a use of it.
/// </para>
/// <para>
/// The classification is deliberately asymmetric. A 2xx settles. A 4xx is a deterministic refusal —
/// policy, an invalid transition, an amount ceiling, a stale revision — and proves the effect did
/// not happen, so it settles as <c>Failed</c>. A 5xx or a dropped connection proves nothing: the
/// business transaction may have committed and the answer been lost on the way back, which is
/// exactly what the <c>AfterBusinessTransactionCommitBeforeResponse</c> checkpoint simulates. Those
/// become <c>Unknown</c> and wait for evidence rather than being retried into a second write.
/// </para>
/// </remarks>
public sealed class ActionExecutor(
    SelfApiClient api,
    AppDbContext db,
    IClock clock,
    ILogger<ActionExecutor> logger)
{
    /// <summary>How long before a reconciler looks at an execution whose answer was lost.</summary>
    public static readonly TimeSpan ReconcileDelay = TimeSpan.FromSeconds(15);

    /// <param name="ifMatch">
    /// The resource revision the approval was bound to, or null for a write nobody had to approve.
    /// </param>
    public async Task<ExecutionAttempt> RunAsync(
        ActionExecution execution,
        ToolCall call,
        long? ifMatch,
        CancellationToken cancellationToken)
    {
        using var activity = AssistantTelemetry.Source.StartActivity("assistant.action.execute");
        activity?.SetTag("assistant.execution_id", execution.Id);
        activity?.SetTag("assistant.tool", execution.ToolName);
        activity?.SetTag("url.path", call.Url);

        // An execution whose answer was lost may already have an answer sitting in the database.
        // Asking costs one indexed read and is the difference between reconciling and re-sending.
        if (await TryReconcileFromReceiptAsync(execution, cancellationToken))
        {
            activity?.SetTag("assistant.attempt", "reconciled_from_receipt");
            return new ExecutionAttempt(execution, StoredPayload(execution));
        }

        // Durable before the request goes out: a crash from here on is resumable, because the row
        // says an attempt was made and under which key.
        var claimed = await TryClaimAttemptAsync(execution.Id, cancellationToken);
        await RefreshAsync(execution, cancellationToken);

        if (!claimed)
        {
            // Somebody else holds this attempt, or it settled while this request was deciding.
            activity?.SetTag("assistant.attempt", "not_claimed");
            return new ExecutionAttempt(execution, StoredPayload(execution));
        }

        activity?.SetTag("assistant.attempt", execution.AttemptCount);

        ApiResponse response;

        try
        {
            response = await api.SendAsync(call.Method, call.Url, call.Body, execution.IdempotencyKey, ifMatch, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            // The request left; the answer did not come back. Nothing here knows whether the write
            // landed, and guessing "it did not" is the expensive direction to be wrong in.
            logger.LogWarning(
                exception, "Execution {ExecutionId} lost its answer from {Path}", execution.Id, call.Url);

            execution.MarkUnknown("transport_lost", exception.Message, clock.UtcNow + ReconcileDelay);
            return await SettleAsync(execution, Ambiguous(execution), activity, cancellationToken);
        }

        if (response.StatusCode is StatusCodes.Status202Accepted && DeliveryIn(response.Payload) is { } deliveryId)
        {
            // The local effect committed and handed off to an outbox. Nothing about the external
            // effect is known yet, so the execution stays running rather than claiming a success
            // the customer has not received.
            execution.AwaitDelivery(deliveryId, response.StatusCode, response.Payload.GetRawText(), clock.UtcNow);
        }
        else if (response.IsSuccess)
        {
            execution.Succeed(response.StatusCode, response.Payload.GetRawText(), clock.UtcNow);
        }
        else if (response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            // A 500 can be raised after the handler's transaction committed but before the response
            // was written. The idempotency receipt is the authority on which of the two happened.
            execution.MarkUnknown(response.Code ?? "api_error", Detail(response), clock.UtcNow + ReconcileDelay);
        }
        else
        {
            execution.Fail(response.StatusCode, response.Code ?? "api_error", Detail(response), clock.UtcNow);
        }

        return await SettleAsync(execution, response.Payload, activity, cancellationToken);
    }

    /// <summary>
    /// Settles an execution whose answer was lost, from the receipt the business transaction wrote.
    /// </summary>
    /// <remarks>
    /// This is what makes <c>Unknown</c> a temporary state rather than a permanent shrug. Because
    /// the receipt is committed in the same transaction as the effect, its presence proves the write
    /// happened and its absence proves it did not — so a completed receipt is authoritative for a
    /// local effect, and no second request is needed to find out.
    /// </remarks>
    public async Task<bool> TryReconcileFromReceiptAsync(ActionExecution execution, CancellationToken cancellationToken)
    {
        if (execution.Status is not ActionExecutionStatus.Unknown)
        {
            return false;
        }

        var key = execution.IdempotencyKey;
        var receipt = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            record => record.UserId == execution.UserId && record.Key == key && record.CompletedAt != null,
            cancellationToken);

        if (receipt is null)
        {
            return false;
        }

        logger.LogInformation(
            "Execution {ExecutionId} reconciled from its idempotency receipt with {Status}.", execution.Id, receipt.StatusCode);

        if (receipt.StatusCode is >= 200 and < 300)
        {
            execution.Succeed(receipt.StatusCode, receipt.ResponseJson, clock.UtcNow);
        }
        else
        {
            execution.Fail(receipt.StatusCode, "api_error", detail: null, clock.UtcNow);
        }

        if (db.Entry(execution).State is EntityState.Detached)
        {
            db.ActionExecutions.Attach(execution).State = EntityState.Modified;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Takes the right to make an attempt, or does not. The bookkeeping is done in the same
    /// statement as the claim so that the count of attempts is the count of requests that actually
    /// went out — not the count of callers who intended one.
    /// </summary>
    private async Task<bool> TryClaimAttemptAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var claimed = await db.ActionExecutions
            .Where(e => e.Id == executionId
                && (e.Status == ActionExecutionStatus.Pending || e.Status == ActionExecutionStatus.Unknown))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(e => e.Status, ActionExecutionStatus.Executing)
                    .SetProperty(e => e.AttemptCount, e => e.AttemptCount + 1)
                    .SetProperty(e => e.StartedAt, e => e.StartedAt ?? now)
                    .SetProperty(e => e.LastAttemptAt, (DateTimeOffset?)now)
                    .SetProperty(e => e.NextAttemptAt, (DateTimeOffset?)null),
                cancellationToken);

        return claimed == 1;
    }

    private async Task<ExecutionAttempt> SettleAsync(
        ActionExecution execution,
        JsonElement payload,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);

        activity?.SetTag("assistant.execution_status", execution.Status.ToString());
        AssistantTelemetry.RecordSettled(execution);

        return new ExecutionAttempt(execution, payload);
    }

    /// <summary>
    /// Brings the caller's instance in line with the row the claim just rewrote. It reloads the
    /// same object rather than fetching a new one, so the caller's reference stays valid and the
    /// change tracker does not end up with two instances of one execution.
    /// </summary>
    private async Task RefreshAsync(ActionExecution execution, CancellationToken cancellationToken)
    {
        if (db.Entry(execution).State is EntityState.Detached)
        {
            db.ActionExecutions.Attach(execution);
        }

        await db.Entry(execution).ReloadAsync(cancellationToken);
    }

    /// <summary>
    /// What a caller who did not get the attempt is shown: the stored result if there is one, and
    /// otherwise a description of where the execution has got to. Never an invented outcome.
    /// </summary>
    private static JsonElement StoredPayload(ActionExecution execution)
    {
        if (execution.ResultJson is { Length: > 0 } json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        return execution.Status is ActionExecutionStatus.Unknown
            ? Ambiguous(execution)
            : JsonSerializer.SerializeToElement(new
            {
                status = execution.Status.ToString().ToLowerInvariant(),
                executionId = execution.Id,
            });
    }

    /// <summary>
    /// What the model is told when the answer was lost. Deliberately not shaped like the other
    /// errors: <c>unknown_outcome</c> is not "it failed", and the description says so in the one
    /// place a model might otherwise reassure somebody that nothing happened.
    /// </summary>
    private static JsonElement Ambiguous(ActionExecution execution) => JsonSerializer.SerializeToElement(new
    {
        error = "unknown_outcome",
        executionId = execution.Id,
        detail = "The request was sent and no answer came back, so it is not known whether it took effect. "
            + "Do not claim it succeeded or failed; say the outcome is being confirmed.",
    });

    /// <summary>
    /// The delivery a <c>202</c> handed off to.
    /// </summary>
    /// <remarks>
    /// The status code carries the meaning, not the field. An invoice detail mentions its delivery
    /// on every response — <c>mark-paid</c> included — and reading that as a hand-off would leave
    /// executions waiting on somebody else's email. <c>202</c> is the endpoint saying "the local
    /// part is done and the rest is on somebody else's network", which is a different outcome from
    /// "done" and has to be classified as one.
    /// </remarks>
    private static Guid? DeliveryIn(JsonElement payload) =>
        payload.ValueKind is JsonValueKind.Object
            && payload.TryGetProperty("delivery", out var delivery)
            && delivery.ValueKind is JsonValueKind.Object
            && delivery.TryGetProperty("id", out var id)
            && id.TryGetGuid(out var deliveryId)
                ? deliveryId
                : null;

    private static string? Detail(ApiResponse response) =>
        response.Payload.ValueKind is JsonValueKind.Object
            && response.Payload.TryGetProperty("detail", out var detail)
            ? detail.GetString()
            : null;
}
