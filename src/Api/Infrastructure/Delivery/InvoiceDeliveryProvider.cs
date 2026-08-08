using System.Collections.Concurrent;

namespace Api.Infrastructure.Delivery;

/// <summary>What the provider says it did, keyed by the client's stable key.</summary>
public sealed record DeliveryReceipt(string ProviderMessageId, DateTimeOffset AcceptedAt);

/// <summary>The invoice, reduced to what a delivery provider needs and nothing more.</summary>
/// <remarks>
/// No line descriptions and no customer notes: this payload is stored in an outbox row and logged
/// by its size, and the invoice body is not something to copy into transport tables.
/// </remarks>
public sealed record InvoiceDeliveryPayload(
    string InvoiceNumber,
    string Recipient,
    string CustomerName,
    decimal Total,
    string Currency,
    DateOnly DueDate);

/// <summary>
/// What a provider can be relied on for, which decides what may be retried after an ambiguous
/// answer.
/// </summary>
/// <remarks>
/// This is not decoration. With <see cref="HonoursStableKey"/> a retry is safe because the provider
/// returns the original receipt; with <see cref="SupportsReceiptLookup"/> the answer can be read
/// back before deciding. With neither, an ambiguous result stays Unknown and waits for a person,
/// and nothing in this repository may describe that as effectively-once delivery.
/// </remarks>
public sealed record ProviderCapabilities(bool HonoursStableKey, bool SupportsReceiptLookup);

/// <summary>
/// The boundary this system cannot make transactional.
/// </summary>
public interface IInvoiceDeliveryProvider
{
    ProviderCapabilities Capabilities { get; }

    /// <exception cref="DeliveryRejectedException">The provider refused it, and will refuse it again.</exception>
    /// <exception cref="AmbiguousDeliveryException">It may have been accepted; the answer was lost.</exception>
    Task<DeliveryReceipt> SendAsync(string providerKey, InvoiceDeliveryPayload payload, CancellationToken cancellationToken);

    /// <summary>The authoritative answer to "did you take this?", for providers that can give one.</summary>
    Task<DeliveryReceipt?> FindReceiptAsync(string providerKey, CancellationToken cancellationToken);
}

/// <summary>A refusal that proves nothing was delivered. Retrying produces the same refusal.</summary>
public sealed class DeliveryRejectedException(string code, string reason) : Exception(reason)
{
    public string Code { get; } = code;
}

/// <summary>
/// Accepted, or not — the caller cannot tell.
/// </summary>
/// <remarks>
/// The most important exception in this feature. It is what a lost response looks like from the
/// sending side, and treating it as a failure is how one invoice gets emailed twice.
/// </remarks>
public sealed class AmbiguousDeliveryException(string reason, Exception? inner = null)
    : Exception(reason, inner);

/// <summary>
/// A deterministic stand-in for an email vendor, holding its receipts on its own side.
/// </summary>
/// <remarks>
/// <para>
/// In memory, and that is the honest shape: these receipts belong to the provider, not to this
/// application, and putting them in our PostgreSQL would quietly make the boundary transactional and
/// make the whole demonstration meaningless. Restarting the process loses them exactly as a real
/// provider's history would be unreachable if it went away.
/// </para>
/// <para>
/// It honours the stable key — a second send with the same key returns the first receipt rather than
/// delivering again — which is the capability that makes reconciliation after a lost response
/// produce one message instead of two.
/// </para>
/// </remarks>
public sealed class DemoInvoiceDeliveryProvider(IClock clock, IFaultInjector faults, ILogger<DemoInvoiceDeliveryProvider> logger)
    : IInvoiceDeliveryProvider
{
    private readonly ConcurrentDictionary<string, DeliveryReceipt> _receipts = new();
    private readonly ConcurrentDictionary<string, int> _requests = new();

    public ProviderCapabilities Capabilities { get; } = new(HonoursStableKey: true, SupportsReceiptLookup: true);

    /// <summary>Everything this provider has ever accepted, by stable key. One entry, one message.</summary>
    public IReadOnlyDictionary<string, DeliveryReceipt> Receipts => _receipts;

    /// <summary>
    /// How many times a key has been sent, as opposed to how many messages it produced.
    /// </summary>
    /// <remarks>
    /// The two numbers together are the demonstration: sent twice, delivered once means the stable
    /// key did its job. Sent once, delivered once means nothing was retried. Only the pair
    /// distinguishes "we were careful" from "we were lucky".
    /// </remarks>
    public int RequestsFor(string providerKey) => _requests.GetValueOrDefault(providerKey);

    public Task<DeliveryReceipt> SendAsync(
        string providerKey,
        InvoiceDeliveryPayload payload,
        CancellationToken cancellationToken)
    {
        _requests.AddOrUpdate(providerKey, 1, (_, count) => count + 1);

        if (string.IsNullOrWhiteSpace(payload.Recipient) || !payload.Recipient.Contains('@'))
        {
            // A deterministic refusal: no amount of retrying produces an address.
            throw new DeliveryRejectedException(
                "no_recipient", $"{payload.CustomerName} has no deliverable email address.");
        }

        // Deduplicated on the client's key, which is what "honours the stable key" means. A retry
        // after a lost answer lands here and gets the original receipt back.
        var receipt = _receipts.GetOrAdd(
            providerKey,
            _ => new DeliveryReceipt($"demo-{Guid.CreateVersion7():N}", clock.UtcNow));

        logger.LogInformation(
            "Demo provider accepted {InvoiceNumber} as {MessageId}", payload.InvoiceNumber, receipt.ProviderMessageId);

        try
        {
            // The receipt is already stored, so a fault here is precisely "accepted, and then the
            // answer was lost" — the case the whole outbox design exists to survive.
            faults.Reach(FaultCheckpoint.AfterProviderAcceptedBeforeReceiptReturned);
        }
        catch (InjectedFaultException exception)
        {
            throw new AmbiguousDeliveryException("The provider accepted the message but the response was lost.", exception);
        }

        return Task.FromResult(receipt);
    }

    public Task<DeliveryReceipt?> FindReceiptAsync(string providerKey, CancellationToken cancellationToken) =>
        Task.FromResult(_receipts.GetValueOrDefault(providerKey));
}
