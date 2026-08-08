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

    /// <summary>Settles what it can and returns how many deliveries it resolved.</summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!provider.Capabilities.SupportsReceiptLookup)
        {
            // Nothing to ask. Leaving these Unknown is the correct outcome, not a gap: an
            // unanswerable question does not become answerable by retrying the request.
            return 0;
        }

        var now = clock.UtcNow;

        var unknown = await db.InvoiceDeliveries
            .Where(delivery => delivery.Status == InvoiceDeliveryStatus.Unknown)
            .OrderBy(delivery => delivery.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var settled = 0;

        foreach (var delivery in unknown)
        {
            using var activity = AssistantTelemetry.Source.StartActivity("assistant.action.reconcile");
            activity?.SetTag("assistant.delivery_id", delivery.Id);
            activity?.SetTag("assistant.execution_id", delivery.ExecutionId);
            activity?.SetTag("assistant.evidence", "provider_receipt");

            var receipt = await provider.FindReceiptAsync(delivery.ProviderKey, cancellationToken);

            if (receipt is null)
            {
                // An authoritative "not found" from a provider that can answer means it never took
                // it, so the outbox row is free to try again on its own schedule.
                activity?.SetTag("assistant.delivery_result", "not_found");
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
        var message = await db.OutboxMessages
            .SingleOrDefaultAsync(m => m.DeliveryId == deliveryId && m.Status == OutboxStatus.Pending, cancellationToken);

        message?.Complete(now);
    }

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
