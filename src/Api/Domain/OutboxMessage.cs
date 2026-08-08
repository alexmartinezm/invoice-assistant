namespace Api.Domain;

public enum OutboxStatus
{
    Pending,
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
        LastError = error is null || error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
    }

    /// <summary>Parks the row without completing it: the delivery is Unknown and the reconciler owns it now.</summary>
    public void AwaitReconciliation(DateTimeOffset availableAt, string? error)
    {
        Defer(availableAt, error);
    }
}
