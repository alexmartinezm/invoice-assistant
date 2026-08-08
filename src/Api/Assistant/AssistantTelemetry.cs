using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Api.Assistant;

public static class AssistantTelemetry
{
    public const string SourceName = "InvoiceAssistant.Assistant";

    public const string MeterName = "InvoiceAssistant.Assistant";

    public static readonly ActivitySource Source = new(SourceName);

    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> ModelCalls = Meter.CreateCounter<long>(
        "assistant.model_calls", description: "Calls made to the model");

    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        "assistant.tokens", description: "Tokens consumed, tagged by direction");

    public static readonly Counter<double> CostEur = Meter.CreateCounter<double>(
        "assistant.cost", unit: "EUR", description: "Model spend as priced by configuration");

    public static readonly Counter<long> BudgetRejections = Meter.CreateCounter<long>(
        "assistant.budget_rejections", description: "Calls or turns refused because the daily budget is spent");

    public static readonly Counter<long> UnpricedModelCalls = Meter.CreateCounter<long>(
        "assistant.unpriced_model_calls", description: "Calls recorded at zero cost because the model has no configured price");

    // --- Durable actions (F5) -------------------------------------------------------------------
    // Started and settled are counted separately rather than as one gauge, because the number worth
    // alerting on is the gap between them: executions that began and never reached a terminal state.

    public static readonly Counter<long> ExecutionsStarted = Meter.CreateCounter<long>(
        "assistant.executions_started", description: "Assistant writes given a durable execution identity, by tool and decision");

    public static readonly Counter<long> ExecutionsSettled = Meter.CreateCounter<long>(
        "assistant.executions_settled", description: "Executions that reached a terminal or ambiguous state, tagged by status");

    public static readonly Histogram<double> ApprovalToEffectSeconds = Meter.CreateHistogram<double>(
        "assistant.approval_to_effect", unit: "s", description: "Time from a human's approval to a confirmed effect");

    public static readonly Counter<long> IdempotencyOutcomes = Meter.CreateCounter<long>(
        "assistant.idempotency_outcomes", description: "Idempotent write outcomes: replayed, mismatched or in progress");

    public static readonly Counter<long> OutboxDispatched = Meter.CreateCounter<long>(
        "assistant.outbox_dispatched", description: "Outbox rows a worker took and carried to the provider");

    // Gauges rather than counters: how deep the queue is, how long the oldest thing in it has been
    // waiting and how many outcomes nobody has confirmed are all states, not events, and none of
    // them is a number you can add up. They read the last value the outbox worker published rather
    // than querying on collection — a metrics scrape should not put load on the database it measures.
    public static readonly ObservableGauge<long> OutboxQueueDepth = Meter.CreateObservableGauge(
        "assistant.outbox_queue_depth", () => Volatile.Read(ref _queueDepth), description: "Outbox rows still waiting to be dispatched");

    public static readonly ObservableGauge<double> OutboxOldestAgeSeconds = Meter.CreateObservableGauge(
        "assistant.outbox_oldest_age", () => Volatile.Read(ref _oldestQueuedSeconds), unit: "s", description: "Age of the oldest undispatched outbox row");

    public static readonly ObservableGauge<long> UnknownDeliveries = Meter.CreateObservableGauge(
        "assistant.unknown_deliveries", () => Volatile.Read(ref _unknownDeliveries), description: "Deliveries whose outcome the provider has not confirmed");

    private static long _queueDepth;
    private static double _oldestQueuedSeconds;
    private static long _unknownDeliveries;

    /// <summary>Published by the outbox worker after each pass. Cheap, and never stale by more than a tick.</summary>
    public static void PublishQueueState(long depth, double oldestQueuedSeconds, long unknownDeliveries)
    {
        Volatile.Write(ref _queueDepth, depth);
        Volatile.Write(ref _oldestQueuedSeconds, oldestQueuedSeconds);
        Volatile.Write(ref _unknownDeliveries, unknownDeliveries);
    }
}
