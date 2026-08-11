namespace Api.Domain;

public enum OutboxStatus
{
    /// <summary>Due, or waiting for its backoff. The dispatcher claims from here and nowhere else.</summary>
    Pending,

    /// <summary>
    /// Dispatched with an unknown outcome. Parked: the reconciler owns it, the dispatcher must not
    /// see it.
    /// </summary>
    /// <remarks>
    /// This state exists because parking used to mean "Pending with a later AvailableAt", which the
    /// dispatcher happily re-claimed the moment that time passed — turning "we do not know whether
    /// the customer got this email" into a second email. Ambiguity is not a backoff.
    /// </remarks>
    AwaitingReconciliation,

    Completed,
}

/// <summary>
/// Work that has to happen outside the transaction that decided it should.
/// </summary>
/// <remarks>
/// <para>
/// The row is written in the same transaction as the business change, which is the whole trick: if
/// the invoice moved to <c>Sent</c>, this exists, and if it did not, this does not. No provider call
/// happens while that transaction is open — holding a database transaction across somebody else's
/// network would trade a correctness problem for lock contention and an unbounded transaction.
/// </para>
/// <para>
/// What this buys is <strong>at-least-once dispatch</strong>, and nothing more. A worker that dies
/// after the provider accepted will try again; the effect is once only if the provider honours the
/// stable key. That is the difference between "effectively once" and "exactly once", and this
/// codebase does not claim the second.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    public const string InvoiceDeliveryType = "invoice_delivery";

    public const int MaxErrorLength = 500;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Type { get; init; }

    public required string PayloadJson { get; init; }

    public Guid DeliveryId { get; init; }

    /// <summary>Unique: two rows for one delivery would be two chances to send it twice.</summary>
    public required string ProviderKey { get; init; }

    public OutboxStatus Status { get; private set; } = OutboxStatus.Pending;

    /// <summary>
    /// Bumped by the lease claim itself, in SQL, for the same reason an execution's attempt count is:
    /// the number worth having is how many workers actually took the row, not how many intended to.
    /// </summary>
    public int AttemptCount { get; private set; }

    /// <summary>When the worker may next pick it up. Moved forward by backoff after a transient failure.</summary>
    public DateTimeOffset AvailableAt { get; private set; }

    /// <summary>
    /// Which worker holds it, and until when. The lease is what lets a second worker take over from
    /// one that died mid-dispatch without two of them calling the provider at the same moment.
    /// </summary>
    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static OutboxMessage ForDelivery(
        InvoiceDelivery delivery,
        string payloadJson,
        DateTimeOffset now) =>
        new()
        {
            Type = InvoiceDeliveryType,
            PayloadJson = payloadJson,
            DeliveryId = delivery.Id,
            ProviderKey = delivery.ProviderKey,
            AvailableAt = now,
            CreatedAt = now,
        };

    /// <summary>Settled, whichever way. The outbox is transport work; the outcome lives on the delivery.</summary>
    public void Complete(DateTimeOffset now)
    {
        Status = OutboxStatus.Completed;
        LeaseOwner = null;
        LeaseExpiresAt = null;
        CompletedAt = now;
    }

    /// <summary>
    /// Puts it back for a later attempt. Used only for failures known to have happened
    /// <em>before</em> the provider accepted anything — an ambiguous result is left to the
    /// reconciler instead, because retrying it is how one email becomes two.
    /// </summary>
    public void Defer(DateTimeOffset availableAt, string? error)
    {
        AvailableAt = availableAt;
        LeaseOwner = null;
        LeaseExpiresAt = null;
        LastError = Bounded(error);
    }

    /// <summary>
    /// Parks the row: the delivery is Unknown and only the reconciler may touch it.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Defer"/>. A deferred row is one we know was never delivered and
    /// may safely be sent again; this one may already be in somebody's inbox. The status change is
    /// what removes it from the dispatcher's claim — <c>AvailableAt</c> alone never could, because
    /// waiting is exactly what makes a deferred row eligible again.
    /// </remarks>
    public void AwaitReconciliation(DateTimeOffset reconcileAt, string? error)
    {
        Status = OutboxStatus.AwaitingReconciliation;
        AvailableAt = reconcileAt;
        LeaseOwner = null;
        LeaseExpiresAt = null;
        LastError = Bounded(error);
    }

    /// <summary>
    /// Returns a parked row to the queue, once something authoritative has established that the
    /// provider never took it — or that sending again is safe because it deduplicates.
    /// </summary>
    public void ResumeAfterReconciliation(DateTimeOffset availableAt)
    {
        Status = OutboxStatus.Pending;
        AvailableAt = availableAt;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    /// <summary>Takes the row for one worker, so the reconciler and the dispatcher cannot both act.</summary>
    public void Lease(string owner, DateTimeOffset expiresAt)
    {
        LeaseOwner = owner;
        LeaseExpiresAt = expiresAt;
    }

    private static string? Bounded(string? error) =>
        error is null || error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}
