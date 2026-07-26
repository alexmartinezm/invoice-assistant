using Api.Domain;

namespace Api.Features.Invoices;

public sealed record InvoiceSummary(
    string Number,
    string CustomerName,
    string Status,
    bool IsOverdue,
    int DaysOverdue,
    DateOnly IssueDate,
    DateOnly DueDate,
    decimal Total);

public sealed record InvoiceLineView(string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

public sealed record InvoiceDetail(
    string Number,
    Guid CustomerId,
    string CustomerName,
    string CustomerTaxId,
    string Status,
    bool IsOverdue,
    int DaysOverdue,
    DateOnly IssueDate,
    DateOnly DueDate,
    IReadOnlyList<InvoiceLineView> Lines,
    decimal Subtotal,
    decimal VatRate,
    decimal VatAmount,
    decimal Total,
    DateTimeOffset? PaidAt);

/// <summary>
/// Envelope for list responses. <c>asOf</c> travels with the payload because "overdue" is only
/// meaningful against a date, and this payload is read by the model as-is.
/// </summary>
public sealed record InvoiceList(DateOnly AsOf, string Currency, int Count, IReadOnlyList<InvoiceSummary> Invoices);

public sealed record NewInvoiceLineRequest(string Description, decimal Quantity, decimal UnitPrice);

/// <summary>
/// The customer can be given by id or by name: the assistant's tool takes a name, and resolving
/// it is the server's job, not the model's.
/// </summary>
public sealed record CreateInvoiceRequest(
    Guid? CustomerId,
    string? CustomerName,
    DateOnly? IssueDate,
    DateOnly? DueDate,
    decimal? VatRate,
    IReadOnlyList<NewInvoiceLineRequest> Lines);

public sealed record UpdateDueDateRequest(DateOnly DueDate);

public static class InvoiceMapping
{
    public static InvoiceSummary ToSummary(this Invoice invoice, DateOnly today) => new(
        invoice.Number,
        invoice.Customer?.Name ?? string.Empty,
        invoice.Status.ToString(),
        invoice.IsOverdue(today),
        invoice.DaysOverdue(today),
        invoice.IssueDate,
        invoice.DueDate,
        invoice.Total);

    public static InvoiceDetail ToDetail(this Invoice invoice, DateOnly today) => new(
        invoice.Number,
        invoice.CustomerId,
        invoice.Customer?.Name ?? string.Empty,
        invoice.Customer?.TaxId ?? string.Empty,
        invoice.Status.ToString(),
        invoice.IsOverdue(today),
        invoice.DaysOverdue(today),
        invoice.IssueDate,
        invoice.DueDate,
        [.. invoice.Lines.Select(l => new InvoiceLineView(l.Description, l.Quantity, l.UnitPrice, l.Amount))],
        invoice.Subtotal,
        invoice.VatRate,
        invoice.VatAmount,
        invoice.Total,
        invoice.PaidAt);
}
