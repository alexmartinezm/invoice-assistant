using System.Text.Json;
using Api.Domain;

namespace Api.Assistant.Tools;

/// <summary>What a business response means for the execution that produced it.</summary>
public enum ExecutionVerdict
{
    /// <summary>Settled: the effect is confirmed.</summary>
    Succeeded,

    /// <summary>The local write committed and an outbox row owns the rest.</summary>
    AwaitingDelivery,

    /// <summary>A deterministic refusal proves the effect did not happen.</summary>
    Failed,

    /// <summary>The answer proves nothing either way.</summary>
    Unknown,
}

/// <summary>
/// The single reading of a business response, shared by the live call and by receipt replay.
/// </summary>
/// <remarks>
/// <para>
/// There used to be two. The live path knew that <c>202</c> with a delivery id means the outbox owns
/// the outcome, and the receipt path treated every <c>2xx</c> as success — so an execution whose
/// response was lost came back from its receipt as <c>Succeeded</c> while the email was still
/// queued, and the outbox could not correct it because a settled execution is terminal. Replaying a
/// stored answer has to mean the same thing as hearing it the first time, so there is one function.
/// </para>
/// <para>
/// The asymmetry between 4xx and 5xx is deliberate and is the heart of the classification: a 4xx is
/// a decision the server made and stands by, while a 5xx may have been raised after the handler's
/// transaction committed but before the response was written.
/// </para>
/// </remarks>
public readonly record struct ExecutionOutcome(ExecutionVerdict Verdict, Guid? DeliveryId)
{
    public static ExecutionOutcome Classify(int statusCode, JsonElement payload)
    {
        if (statusCode is StatusCodes.Status202Accepted && DeliveryIn(payload) is { } deliveryId)
        {
            return new ExecutionOutcome(ExecutionVerdict.AwaitingDelivery, deliveryId);
        }

        if (statusCode is >= 200 and < 300)
        {
            return new ExecutionOutcome(ExecutionVerdict.Succeeded, null);
        }

        return statusCode >= StatusCodes.Status500InternalServerError
            ? new ExecutionOutcome(ExecutionVerdict.Unknown, null)
            : new ExecutionOutcome(ExecutionVerdict.Failed, null);
    }

    /// <summary>Classifies a stored receipt, whose body is text rather than a parsed element.</summary>
    public static ExecutionOutcome ClassifyStored(int statusCode, string? responseJson)
    {
        if (responseJson is not { Length: > 0 })
        {
            return Classify(statusCode, default);
        }

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            return Classify(statusCode, document.RootElement);
        }
        catch (JsonException)
        {
            // A receipt we cannot read is not a receipt we may act on optimistically.
            return new ExecutionOutcome(ExecutionVerdict.Unknown, null);
        }
    }

    /// <summary>Applies the verdict to the execution, so the two paths also *record* it identically.</summary>
    public void ApplyTo(ActionExecution execution, int statusCode, string? resultJson, string? errorCode, string? detail, DateTimeOffset now, TimeSpan reconcileDelay)
    {
        switch (Verdict)
        {
            case ExecutionVerdict.AwaitingDelivery:
                execution.AwaitDelivery(DeliveryId!.Value, statusCode, resultJson, now);
                break;

            case ExecutionVerdict.Succeeded:
                execution.Succeed(statusCode, resultJson, now);
                break;

            case ExecutionVerdict.Unknown:
                execution.MarkUnknown(errorCode ?? "api_error", detail, now + reconcileDelay);
                break;

            default:
                execution.Fail(statusCode, errorCode ?? "api_error", detail, now);
                break;
        }
    }

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
            && id.TryGetGuid(out var value)
            ? value
            : null;
}
