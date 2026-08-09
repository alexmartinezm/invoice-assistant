namespace Api.Domain;

/// <summary>
/// What became of an attempt to hand an invoice to the delivery provider.
/// </summary>
/// <remarks>
/// Separate from <see cref="InvoiceStatus"/> on purpose. <c>Sent</c> is a fact about the ledger —
/// the invoice has left draft and the customer owes money — and it commits locally. Whether the
/// customer actually received anything is a fact about somebody else's system, and it cannot commit
/// with ours. Collapsing the two would let an invoice read <c>Sent</c> while the email sat in a
/// queue, or worse, after the provider rejected it.
/// </remarks>
public enum InvoiceDeliveryStatus
{
    /// <summary>Committed locally and waiting for the outbox worker.</summary>
    Queued,

    Delivered,

    /// <summary>The provider refused it, authoritatively. Retrying would be refused identically.</summary>
    Failed,

    /// <summary>The provider may have taken it. Not retried blindly; reconciled from a receipt.</summary>
    Unknown,
}

/// <summary>Durable business history for one delivery of one invoice.</summary>
public sealed class InvoiceDelivery
{
    public const int MaxErrorLength = 500;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid InvoiceId { get; init; }

    /// <summary>Denormalised so delivery history reads without joining a possibly-renumbered ledger.</summary>
    public required string InvoiceNumber { get; init; }

    /// <summary>The assistant execution this delivery settles, when one is waiting on it.</summary>
    public Guid? ExecutionId { get; init; }

    /// <summary>
    /// The key the provider deduplicates on. Stable for the life of the delivery: it is what turns
    /// at-least-once dispatch into an effectively-once effect, and only for a provider that honours
    /// it. Unique, so two outbox rows cannot come to represent the same delivery.
    /// </summary>
    public required string ProviderKey { get; init; }

    public required string Recipient { get; init; }

    public InvoiceDeliveryStatus Status { get; private set; } = InvoiceDeliveryStatus.Queued;

    public string? ProviderMessageId { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? SettledAt { get; private set; }

    public bool IsSettled => Status is InvoiceDeliveryStatus.Delivered or InvoiceDeliveryStatus.Failed;

    public void Attempted() => Attempts++;

    public void Delivered(string providerMessageId, DateTimeOffset now)
    {
        Status = InvoiceDeliveryStatus.Delivered;
        ProviderMessageId = providerMessageId;
        LastError = null;
        SettledAt = now;
    }

    public void Rejected(string error, DateTimeOffset now)
    {
        Status = InvoiceDeliveryStatus.Failed;
        LastError = Truncate(error);
        SettledAt = now;
    }

    /// <summary>
    /// The provider may or may not have it. Deliberately leaves <see cref="SettledAt"/> null: this
    /// is not an outcome, it is the absence of one, and the reconciler is what turns it into either.
    /// </summary>
    public void Ambiguous(string error)
    {
        Status = InvoiceDeliveryStatus.Unknown;
        LastError = Truncate(error);
    }

    /// <summary>
    /// The provider has told us, authoritatively, that it never received this — so the ambiguity is
    /// resolved in the one direction that makes sending again correct rather than duplicating.
    /// </summary>
    public void Requeued()
    {
        Status = InvoiceDeliveryStatus.Queued;
    }

    private static string? Truncate(string? error) =>
        error is null || error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}
