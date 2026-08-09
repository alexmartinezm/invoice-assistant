using Api.Assistant;
using Api.Domain;
using Api.Features.Actions;
using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure.Delivery;

/// <summary>
/// Turns "we do not know" into an answer, by asking the only party that does.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that makes <see cref="InvoiceDeliveryStatus.Unknown"/> a temporary state
/// rather than a permanent shrug — and the reason the system can be honest about ambiguity instead
/// of resolving it by guessing. A retry would also produce an answer; it would just produce a second
/// email along the way.
/// </para>
/// <para>
/// What it can do depends entirely on the provider. With receipt lookup it asks. With a stable key
/// but no lookup, a retry is safe because the provider deduplicates. With neither, the delivery
/// stays Unknown and waits for a person, and nothing here pretends otherwise.
/// </para>
/// </remarks>
public sealed class DeliveryReconciler(
    AppDbContext db,
    IInvoiceDeliveryProvider provider,
    ActionNarrator narrator,
    IClock clock,
    ILogger<DeliveryReconciler> logger)
{
    /// <summary>How long to wait before asking. Long enough for a slow provider to finish writing.</summary>
    public static readonly TimeSpan ReconcileDelay = TimeSpan.FromSeconds(5);

    public const int BatchSize = 20;

    /// <summary>Matches the dispatcher's, so neither can quietly outlive the other's claim.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    /// <summary>Settles what it can and returns how many deliveries it resolved.</summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (!provider.Capabilities.SupportsReceiptLookup)
        {
            // Nothing to ask. With a stable key a resend is still safe, so the parked rows can go
            // back to the queue; without one they stay parked and wait for a person. Neither branch
            // guesses, and neither sends a second email on the strength of a shrug.
            return provider.Capabilities.HonoursStableKey
                ? await ResumeParkedAsync(now, cancellationToken)
                : 0;
        }

        var unknown = await db.InvoiceDeliveries
            .Where(delivery => delivery.Status == InvoiceDeliveryStatus.Unknown)
            .OrderBy(delivery => delivery.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var settled = 0;
        var owner = $"reconciler:{Environment.MachineName}:{Guid.CreateVersion7():N}";

        foreach (var delivery in unknown)
        {
            // Take the outbox row before touching the provider. Without this the reconciler and the
            // dispatcher could both be holding the same delivery, and the reconciler's write would
            // clear a lease the dispatcher was still working under.
            if (!await TryLeaseAsync(delivery.Id, owner, now, cancellationToken))
            {
                continue;
            }

            using var activity = AssistantTelemetry.Source.StartActivity("assistant.action.reconcile");
            activity?.SetTag("assistant.delivery_id", delivery.Id);
            activity?.SetTag("assistant.execution_id", delivery.ExecutionId);
            activity?.SetTag("assistant.evidence", "provider_receipt");

            var receipt = await provider.FindReceiptAsync(delivery.ProviderKey, cancellationToken);

            if (receipt is null)
            {
                // An authoritative "not found" from a provider that can answer means it never took
                // it, so the outbox row is free to try again on its own schedule. This is the only
                // route back into the queue for a provider with lookup: a resend now sends something
                // the provider has told us it never received.
                logger.LogInformation("Delivery {DeliveryId} was never received; returning it to the queue.", delivery.Id);

                delivery.Requeued();
                await ResumeOutboxAsync(delivery.Id, now, cancellationToken);
                activity?.SetTag("assistant.delivery_result", "not_found");
                settled++;
                continue;
            }

            logger.LogInformation(
                "Delivery {DeliveryId} reconciled to delivered as {MessageId}", delivery.Id, receipt.ProviderMessageId);

            delivery.Delivered(receipt.ProviderMessageId, now);
            await CompleteOutboxAsync(delivery.Id, now, cancellationToken);
            await SettleExecutionAsync(delivery, receipt, cancellationToken);

            activity?.SetTag("assistant.delivery_result", "delivered");
            settled++;
        }

        if (settled > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return settled;
    }

    private async Task CompleteOutboxAsync(Guid deliveryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var message = await OutboxFor(deliveryId, cancellationToken);
        message?.Complete(now);
    }

    /// <summary>
    /// Claims the parked row for this reconciler, refusing if a live lease says somebody else has it.
    /// </summary>
    private async Task<bool> TryLeaseAsync(
        Guid deliveryId,
        string owner,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var leased = await db.OutboxMessages
            .Where(message => message.DeliveryId == deliveryId
                && message.Status == OutboxStatus.AwaitingReconciliation
                && (message.LeaseExpiresAt == null || message.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.LeaseOwner, owner)
                    .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)(now + LeaseDuration)),
                cancellationToken);

        return leased == 1;
    }

    private async Task ResumeOutboxAsync(Guid deliveryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var message = await OutboxFor(deliveryId, cancellationToken);
        message?.ResumeAfterReconciliation(now);
    }

    /// <summary>
    /// Returns every parked row to the queue. Only reachable for a provider that deduplicates on the
    /// stable key, which is what makes sending again a repeat rather than a second email.
    /// </summary>
    private async Task<int> ResumeParkedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var parked = await db.OutboxMessages
            .Where(message => message.Status == OutboxStatus.AwaitingReconciliation && message.AvailableAt <= now)
            .OrderBy(message => message.AvailableAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in parked)
        {
            message.ResumeAfterReconciliation(now);
        }

        if (parked.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return parked.Count;
    }

    private Task<OutboxMessage?> OutboxFor(Guid deliveryId, CancellationToken cancellationToken) =>
        db.OutboxMessages.SingleOrDefaultAsync(
            m => m.DeliveryId == deliveryId && m.Status != OutboxStatus.Completed, cancellationToken);

    private async Task SettleExecutionAsync(
        InvoiceDelivery delivery,
        DeliveryReceipt receipt,
        CancellationToken cancellationToken)
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

        execution.RecordProviderMessage(receipt.ProviderMessageId);
        execution.Succeed(StatusCodes.Status200OK, execution.ResultJson, clock.UtcNow);

        AssistantTelemetry.RecordSettled(execution);

        narrator.RecordClosingLine(
            execution.ConversationId, await narrator.DescribeSettledAsync(execution, cancellationToken));
    }
}
