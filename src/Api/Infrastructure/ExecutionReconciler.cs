using Api.Assistant;
using Api.Assistant.Tools;
using Api.Domain;
using Api.Features.Actions;
using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure;

/// <summary>
/// Finishes executions whose caller never came back.
/// </summary>
/// <remarks>
/// <para>
/// Every other path through an execution belongs to a request: somebody approved, somebody polled,
/// somebody retried. This one belongs to nobody, which is exactly why it is needed — an approval
/// whose process died has no request left to notice, and until now the row sat in
/// <c>Executing</c> or <c>Unknown</c> for ever with <c>NextAttemptAt</c> written and nothing
/// reading it.
/// </para>
/// <para>
/// <strong>It settles from evidence and never re-executes.</strong> It holds no bearer token and
/// must never acquire one: re-running somebody's write under a synthesised identity is precisely
/// the thing ADR 009 forbids. The evidence it can use is the idempotency receipt, which is
/// authoritative for a local effect because it commits with that effect, and the delivery, which is
/// authoritative for the external one. Where neither answers, the execution stays unsettled and a
/// person decides — which is the honest outcome, not a gap.
/// </para>
/// </remarks>
public sealed class ExecutionReconciler(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ExecutionReconciler> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    public const int BatchSize = 50;

    /// <summary>
    /// How much further out to push a genuinely evidence-less execution before checking again.
    /// Grows with the execution's age: a fresh <c>Unknown</c> is worth polling quickly, because most
    /// transient answers show up within seconds, but one that is still unresolved an hour later is
    /// not going to be settled by asking every fifteen seconds — it is going to be settled by a
    /// person, and until then this is just how often the row is willing to be looked at again.
    /// </summary>
    private static TimeSpan ReconcileBackoff(DateTimeOffset createdAt, DateTimeOffset now)
    {
        var age = now - createdAt;

        if (age < TimeSpan.FromMinutes(1))
        {
            return TimeSpan.FromSeconds(15);
        }

        if (age < TimeSpan.FromMinutes(10))
        {
            return TimeSpan.FromMinutes(1);
        }

        return age < TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed pass is not a reason to stop reconciling; the next tick tries again.
                logger.LogWarning(exception, "An execution reconciliation pass failed.");
            }
        }
    }

    /// <summary>Settles what the evidence allows and returns how many executions moved.</summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<ActionExecutor>();
        var narrator = scope.ServiceProvider.GetRequiredService<ActionNarrator>();
        var now = clock.UtcNow;

        var stalled = await db.ActionExecutions
            .Where(execution =>
                // Answer lost, and the reconcile delay has passed.
                (execution.Status == ActionExecutionStatus.Unknown
                    && execution.NextAttemptAt != null
                    && execution.NextAttemptAt <= now)
                // Or an attempt whose holder never came back. Note the AttemptExpiresAt != null
                // guard: an execution waiting on a delivery has no lease and is the outbox's, not
                // ours.
                || (execution.Status == ActionExecutionStatus.Executing
                    && execution.AttemptExpiresAt != null
                    && execution.AttemptExpiresAt <= now))
            .OrderBy(execution => execution.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var settled = 0;

        foreach (var execution in stalled)
        {
            using var activity = AssistantTelemetry.Source.StartActivity("assistant.action.reconcile");
            activity?.SetTag("assistant.execution_id", execution.Id);
            activity?.SetTag("assistant.tool", execution.ToolName);
            activity?.SetTag("assistant.evidence", "idempotency_receipt");

            var moved = true;

            if (await SettleFromDeliveryAsync(db, execution, cancellationToken)
                || await executor.TryReconcileFromReceiptAsync(execution, cancellationToken))
            {
                activity?.SetTag("assistant.execution_status", execution.Status.ToString());

                if (execution.IsSettled)
                {
                    narrator.RecordClosingLine(
                        execution.ConversationId, await narrator.DescribeSettledAsync(execution, cancellationToken));
                }
            }
            else if (execution.Status is ActionExecutionStatus.Executing)
            {
                // No evidence either way. Release the abandoned attempt so a caller can resume it
                // under their own identity, and leave the outcome unstated rather than invented.
                //
                // NextAttemptAt is set to now, not to a further delay: this pass already did the
                // checking a delay exists to wait out — it looked for a receipt and a settled
                // delivery and found neither — so this *is* the "explicit resume decision" that lets
                // a caller claim it immediately, not a fresh Unknown that might still be racing a
                // live request nobody has checked on yet.
                logger.LogWarning(
                    "Execution {ExecutionId} was abandoned mid-attempt with no receipt; releasing it for resume.",
                    execution.Id);

                execution.MarkUnknown("attempt_abandoned", "The attempt was not completed.", now);
            }
            else
            {
                // Already Unknown, and still no evidence. Nothing was learned, so nothing about the
                // execution changes — only when this row is willing to be looked at again, which is
                // what stops it from being reselected first on every single pass and crowding out
                // ones the evidence above could actually settle.
                execution.DeferReconciliation(now + ReconcileBackoff(execution.CreatedAt, now));
                moved = false;
            }

            // Saved per execution, not batched at the end: a concurrency conflict on one row — the
            // live path settling the same execution between this pass reading it and writing it back
            // — must not also roll back everything this pass already resolved for every other row in
            // the batch, and reconciling from receipt or delivery is on this same context per
            // iteration regardless, so there is nothing gained by deferring the save.
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                logger.LogInformation(
                    exception,
                    "Execution {ExecutionId} was settled by something else while this pass was reconciling it.",
                    execution.Id);

                db.Entry(execution).State = EntityState.Detached;
                continue;
            }

            if (moved)
            {
                settled++;
            }
        }

        return settled;
    }

    /// <summary>
    /// A delivery that has already settled is the authority for anything waiting on it — this
    /// catches an execution whose outbox finished while its own row was mid-crash.
    /// </summary>
    private async Task<bool> SettleFromDeliveryAsync(
        AppDbContext db,
        ActionExecution execution,
        CancellationToken cancellationToken)
    {
        if (execution.DeliveryId is not { } deliveryId)
        {
            return false;
        }

        var delivery = await db.InvoiceDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);

        switch (delivery?.Status)
        {
            case InvoiceDeliveryStatus.Delivered:
                execution.RecordProviderMessage(delivery.ProviderMessageId!);
                execution.Succeed(StatusCodes.Status200OK, execution.ResultJson, clock.UtcNow);
                return true;

            case InvoiceDeliveryStatus.Failed:
                execution.Fail(StatusCodes.Status502BadGateway, "delivery_rejected", delivery.LastError, clock.UtcNow);
                return true;

            default:
                return false;
        }
    }
}
