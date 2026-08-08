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
    decimal Total,
    long Revision);

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
    DateTimeOffset? PaidAt,
    long Revision);

/// <summary>
/// Envelope for list responses. <c>asOf</c> travels with the payload because "overdue" is only
/// meaningful against a date, and this payload is read by the model as-is.
/// </summary>
/// <param name="Count">How many invoices this response carries.</param>
/// <param name="Total">
/// How many invoices match the filters in total. Separate from <see cref="Count"/> because a page
/// is not an answer to "how many": a model handed twenty rows off a filter matching sixty has no
/// way to tell, and would report twenty with complete confidence. The count the user is owed comes
/// from the database, which is the same reason the aging report exists (ADR 004).
/// </param>
/// <param name="Truncated">True when the filters match more invoices than this response carries.</param>
public sealed record InvoiceList(
    DateOnly AsOf,
    string Currency,
    int Count,
    int Total,
    bool Truncated,
    IReadOnlyList<InvoiceSummary> Invoices);

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

/// <summary>
/// The <c>If-Match</c> precondition on an invoice write, and the <c>ETag</c> that produces it.
/// </summary>
/// <remarks>
/// An invoice's ETag is its <see cref="Invoice.Revision"/>. Approved assistant writes send the
/// revision captured when the proposal was made, so a write can only land on the invoice the person
/// was actually looking at. A mismatch is <c>412 resource_changed</c> and the server does not
/// refresh and retry: executing against state the user never saw is precisely what asking them was
/// meant to prevent.
/// </remarks>
public static class InvoicePrecondition
{
    public static string ETagFor(long revision) => $"\"{revision}\"";

    /// <summary>
    /// Reads the header, tolerating both the quoted form HTTP specifies and the bare number a
    /// hand-written curl is likely to send. Absent means "no precondition", which is allowed.
    /// </summary>
    public static bool TryRead(HttpRequest request, out long revision)
    {
        revision = 0;

        var value = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return long.TryParse(value.Trim().TrimStart('W', '/').Trim('"'), out revision);
    }

    /// <summary>Null when the write may proceed, otherwise the refusal to return.</summary>
    public static IResult? Check(HttpRequest request, Invoice invoice)
    {
        if (!TryRead(request, out var expected) || expected == invoice.Revision)
        {
            return null;
        }

        return Results.Problem(
            title: "The invoice changed",
            detail: $"{invoice.Number} was at revision {expected} when this was proposed and is now at "
                + $"{invoice.Revision}. Nothing was changed. Ask for it again so the new state can be approved.",
            statusCode: StatusCodes.Status412PreconditionFailed,
            extensions: new Dictionary<string, object?> { ["code"] = "resource_changed" });
    }
}

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
        invoice.Total,
        invoice.Revision);

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
        invoice.PaidAt,
        invoice.Revision);
}
