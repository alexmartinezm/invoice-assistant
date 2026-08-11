using Api.Assistant;
using Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure.Delivery;

/// <summary>
/// The loop that drains the outbox and reconciles what it could not settle.
/// </summary>
/// <remarks>
/// <para>
/// A scope per pass rather than a captured <c>DbContext</c>: a long-lived scoped service in a
/// <c>BackgroundService</c> is the classic way to end up with one connection and a change tracker
/// that grows for the life of the process.
/// </para>
/// <para>
/// Stopping this worker is a safe operational pause. Committed rows stay queued, the invoices stay
/// <c>Sent</c>, and the deliveries stay <c>Queued</c> until it comes back — which is the point of
/// having written them down rather than sending inline.
/// </para>
/// </remarks>
public sealed class OutboxWorker(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<OutboxWorker> logger)
    : BackgroundService
{
    /// <summary>How often the queue is looked at when it is empty. Short: a queued invoice is a person waiting.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatched = 0;

            try
            {
                dispatched = await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed pass is not a reason to stop draining the queue.
                logger.LogError(exception, "An outbox pass failed.");
            }

            if (dispatched == 0)
            {
                await Task.Delay(Idle, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>One pass: dispatch what is due, then settle what came back ambiguous.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        var reconciler = scope.ServiceProvider.GetRequiredService<DeliveryReconciler>();

        var dispatched = await dispatcher.DispatchBatchAsync(cancellationToken);
        var reconciled = await reconciler.ReconcileAsync(cancellationToken);

        if (dispatched > 0)
        {
            AssistantTelemetry.OutboxDispatched.Add(dispatched);
        }

        await PublishQueueStateAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken);

        return dispatched + reconciled;
    }

    /// <summary>
    /// Publishes queue depth, the age of the oldest waiting row and the number of unconfirmed
    /// deliveries.
    /// </summary>
    /// <remarks>
    /// These are the three numbers to watch before pointing the external path at anything real. Queue
    /// depth alone says nothing — a deep queue draining fast is healthy — so the age of the oldest
    /// row is the one that matters, and unconfirmed deliveries is the count of things a person may
    /// eventually have to look at.
    /// </remarks>
    private async Task PublishQueueStateAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var pending = db.OutboxMessages.Where(message => message.Status == OutboxStatus.Pending);

        var depth = await pending.CountAsync(cancellationToken);
        var oldest = depth == 0 ? null : await pending.MinAsync(message => (DateTimeOffset?)message.CreatedAt, cancellationToken);
        var unknown = await db.InvoiceDeliveries.CountAsync(
            delivery => delivery.Status == InvoiceDeliveryStatus.Unknown, cancellationToken);

        AssistantTelemetry.PublishQueueState(
            depth,
            oldest is { } queuedAt ? (clock.UtcNow - queuedAt).TotalSeconds : 0,
            unknown);
    }
}
