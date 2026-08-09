using System.Text.Json;
using Api.Assistant;
using Api.Domain;
using Api.Features.Actions;
using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure.Delivery;

/// <summary>
/// Takes committed outbox work and hands it to the provider, exactly once per lease.
/// </summary>
/// <remarks>
/// <para>
/// The shape is the point. Rows are claimed in a short transaction with
/// <c>FOR UPDATE SKIP LOCKED</c>, and the row lock is released <em>before</em> the provider is
/// called — a lease, written into the row, is what stops a second worker taking it, rather than a
/// database lock held across somebody else's network.
/// </para>
/// <para>
/// The three outcomes are not symmetric, and that asymmetry is the feature. Accepted settles.
/// A deterministic rejection settles as failed and is never retried, because it would be refused
/// identically. An ambiguous answer settles as nothing: the row is parked for the reconciler, which
/// asks the provider what it actually has. Retrying an ambiguous send is how one invoice gets
/// emailed twice.
/// </para>
/// </remarks>
public sealed class OutboxDispatcher(
    AppDbContext db,
    IInvoiceDeliveryProvider provider,
    ActionNarrator narrator,
    IClock clock,
    IFaultInjector faults,
    ILogger<OutboxDispatcher> logger)
{
    public const int BatchSize = 10;

    /// <summary>Long enough for a slow provider, short enough that a dead worker's rows come back.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>Backoff for failures known to have happened before the provider took anything.</summary>
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Dispatches one batch and returns how many rows it took. Zero means the queue is quiet.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var owner = $"{Environment.MachineName}:{Guid.CreateVersion7():N}";
        var claimed = await ClaimAsync(owner, cancellationToken);

        foreach (var message in claimed)
        {
            await DispatchAsync(message, cancellationToken);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Leases due rows. <c>SKIP LOCKED</c> is what makes two workers a throughput decision rather
    /// than a correctness one: each takes rows the other did not.
    /// </summary>
    private async Task<List<OutboxMessage>> ClaimAsync(string owner, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE outbox_messages
            SET "LeaseOwner" = {owner},
                "LeaseExpiresAt" = {now + LeaseDuration},
                "AttemptCount" = "AttemptCount" + 1
            WHERE "Id" IN (
                SELECT "Id" FROM outbox_messages
                WHERE "Status" = 'Pending'
                  AND "AvailableAt" <= {now}
                  AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= {now})
                ORDER BY "AvailableAt", "CreatedAt"
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
            )
            """,
            cancellationToken);

        // The owner is unique per batch, so reading it back is equivalent to a RETURNING clause and
        // does not depend on the provider supporting one.
        return await db.OutboxMessages.Where(message => message.LeaseOwner == owner).ToListAsync(cancellationToken);
    }

    private async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var activity = AssistantTelemetry.Source.StartActivity("assistant.outbox.dispatch");
        activity?.SetTag("assistant.outbox_id", message.Id);
        activity?.SetTag("assistant.delivery_id", message.DeliveryId);
        activity?.SetTag("assistant.attempt", message.AttemptCount);
        activity?.SetTag("assistant.provider", provider.GetType().Name);

        var delivery = await db.InvoiceDeliveries.SingleOrDefaultAsync(d => d.Id == message.DeliveryId, cancellationToken);

        if (delivery is null)
        {
            logger.LogError("Outbox message {MessageId} points at a delivery that does not exist.", message.Id);
            message.Complete(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var payload = JsonSerializer.Deserialize<InvoiceDeliveryPayload>(message.PayloadJson, Json)!;
        delivery.Attempted();

        // Nothing is holding a row lock by this point: the claim committed, and the lease is what
        // keeps the row ours.
        faults.Reach(FaultCheckpoint.AfterOutboxClaimBeforeProviderCall);

        try
        {
            var receipt = await provider.SendAsync(delivery.ProviderKey, payload, cancellationToken);

            faults.Reach(FaultCheckpoint.AfterProviderReceiptBeforeExecutionFinalized);

            delivery.Delivered(receipt.ProviderMessageId, clock.UtcNow);
            message.Complete(clock.UtcNow);
            activity?.SetTag("assistant.delivery_result", "delivered");
        }
        catch (DeliveryRejectedException rejection)
        {
            // Authoritative: nothing was delivered and nothing will be. Completing the row rather
            // than deferring it is the difference between a failure and a retry loop.
            logger.LogWarning("Delivery {DeliveryId} was rejected: {Code}", delivery.Id, rejection.Code);

            delivery.Rejected($"{rejection.Code}: {rejection.Message}", clock.UtcNow);
            message.Complete(clock.UtcNow);
            activity?.SetTag("assistant.delivery_result", "rejected");
        }
        catch (Exception exception) when (exception is AmbiguousDeliveryException or InjectedFaultException)
        {
            // It may be delivered. The reconciler asks the provider; this worker does not guess, and
            // above all does not send again.
            logger.LogWarning(exception, "Delivery {DeliveryId} has an unknown outcome.", delivery.Id);

            delivery.Ambiguous(exception.Message);
            message.AwaitReconciliation(clock.UtcNow + DeliveryReconciler.ReconcileDelay, exception.Message);
            activity?.SetTag("assistant.delivery_result", "unknown");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            // A transport failure after the request went out. Whether the provider saw it is exactly
            // what we cannot know: a connection dropped mid-flight looks identical whether it broke
            // before or after the far end read the bytes. Only a provider that deduplicates makes a
            // resend safe; anything else is parked for the reconciler.
            if (provider.Capabilities.HonoursStableKey)
            {
                logger.LogWarning(exception, "Delivery {DeliveryId} could not be dispatched; the stable key makes a retry safe.", delivery.Id);

                message.Defer(clock.UtcNow + Backoff(message.AttemptCount), exception.Message);
                activity?.SetTag("assistant.delivery_result", "deferred");
            }
            else
            {
                logger.LogWarning(exception, "Delivery {DeliveryId} was lost in transport and cannot be safely retried.", delivery.Id);

                delivery.Ambiguous(exception.Message);
                message.AwaitReconciliation(clock.UtcNow + DeliveryReconciler.ReconcileDelay, exception.Message);
                activity?.SetTag("assistant.delivery_result", "unknown");
            }
        }

        await SettleExecutionAsync(delivery, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Moves the assistant execution waiting on this delivery, and writes the conversation's closing
    /// line when it settles. Until now that line was written by the approval request; a delivery
    /// settles minutes later, so whoever settles it owes the sentence.
    /// </summary>
    private async Task SettleExecutionAsync(InvoiceDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.ExecutionId is not { } executionId)
        {
            return;
        }

        var execution = await db.ActionExecutions.SingleOrDefaultAsync(e => e.Id == executionId, cancellationToken);
        if (execution is null || execution.IsSettled)
        {
            return;
        }

        switch (delivery.Status)
        {
            case InvoiceDeliveryStatus.Delivered:
                execution.RecordProviderMessage(delivery.ProviderMessageId!);
                execution.Succeed(StatusCodes.Status200OK, execution.ResultJson, clock.UtcNow);
                break;

            case InvoiceDeliveryStatus.Failed:
                execution.Fail(StatusCodes.Status502BadGateway, "delivery_rejected", delivery.LastError, clock.UtcNow);
                break;

            case InvoiceDeliveryStatus.Unknown:
                execution.MarkUnknown("delivery_unknown", delivery.LastError, clock.UtcNow + DeliveryReconciler.ReconcileDelay);
                break;

            default:
                return;
        }

        AssistantTelemetry.RecordSettled(execution);

        if (execution.IsSettled)
        {
            narrator.RecordClosingLine(
                execution.ConversationId, await narrator.DescribeSettledAsync(execution, cancellationToken));
        }
    }

    /// <summary>Exponential with jitter, capped. Jitter so a provider outage does not produce a thundering herd.</summary>
    private static TimeSpan Backoff(int attempt)
    {
        var exponential = BaseBackoff * Math.Pow(2, Math.Min(attempt, 8));
        var jitter = Random.Shared.NextDouble() * 0.3 + 0.85;

        return exponential > MaxBackoff ? MaxBackoff : exponential * jitter;
    }
}
