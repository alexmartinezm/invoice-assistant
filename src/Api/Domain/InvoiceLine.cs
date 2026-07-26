namespace Api.Domain;

public sealed class InvoiceLine
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid InvoiceId { get; init; }

    public required string Description { get; init; }

    public decimal Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    /// <summary>
    /// Quantity × unit price. Persisted for reporting convenience but never accepted from a
    /// caller: <see cref="Recalculate"/> is the only writer, so the model cannot forge a total.
    /// </summary>
    public decimal Amount { get; private set; }

    internal void Recalculate() =>
        Amount = decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}
