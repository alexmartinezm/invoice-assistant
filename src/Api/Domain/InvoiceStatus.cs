namespace Api.Domain;

/// <summary>
/// Persisted invoice states. <c>Overdue</c> is deliberately absent: it is derived from
/// <see cref="Invoice.DueDate"/> against the current date, never stored.
/// </summary>
public enum InvoiceStatus
{
    Draft,
    Sent,
    Paid,
    Cancelled,
}
