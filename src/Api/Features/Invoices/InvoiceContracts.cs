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

/// <summary>
/// Where the invoice's delivery got to, reported separately from the invoice's own status.
/// </summary>
/// <remarks>
/// <c>Sent</c> is a fact about our ledger and commits locally; whether the customer received
/// anything is a fact about somebody else's system. An invoice that reads Sent while its delivery
/// reads Queued, Failed or Unknown is not a contradiction — it is the truth, and hiding it behind
/// one word is what would be dishonest.
/// </remarks>
public sealed record InvoiceDeliveryView(
    Guid Id,
    string Status,
    int Attempts,
    string? ProviderMessageId,
    DateTimeOffset? SettledAt);

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
    long Revision,
    InvoiceDeliveryView? Delivery);

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

/// <summary>Whether an <c>If-Match</c> header carried a precondition, and if so, whether it parsed.</summary>
public enum PreconditionHeader
{
    /// <summary>No header at all. Allowed: an unconditional write has no precondition to check.</summary>
    Absent,

    Valid,

    /// <summary>
    /// Present but not a revision this server wrote. Distinct from <see cref="Absent"/> on purpose —
    /// a header the caller bothered to send and got wrong is a caller bug, and folding it into "no
    /// precondition" would silently run the write the header was there to stop.
    /// </summary>
    Invalid,
}

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
    /// hand-written curl is likely to send.
    /// </summary>
    public static PreconditionHeader TryRead(HttpRequest request, out long revision)
    {
        revision = 0;

        var value = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return PreconditionHeader.Absent;
        }

        return long.TryParse(value.Trim().TrimStart('W', '/').Trim('"'), out revision)
            ? PreconditionHeader.Valid
            : PreconditionHeader.Invalid;
    }

    /// <summary>Null when the write may proceed, otherwise the refusal to return.</summary>
    public static IResult? Check(HttpRequest request, Invoice invoice)
    {
        var header = TryRead(request, out var expected);

        if (header is PreconditionHeader.Absent)
        {
            return null;
        }

        if (header is PreconditionHeader.Invalid)
        {
            // Not "no precondition" — a header the caller sent and got wrong must not silently
            // downgrade to an unconditional write, which is what treating it as absent would do.
            return Results.Problem(
                title: "Malformed If-Match",
                detail: $"The If-Match header on this request could not be read as a revision number. "
                    + $"Send the exact value from the invoice's own ETag, or omit the header.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_precondition" });
        }

        if (expected == invoice.Revision)
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

    public static InvoiceDetail ToDetail(this Invoice invoice, DateOnly today, InvoiceDelivery? delivery = null) => new(
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
        invoice.Revision,
        delivery is null
            ? null
            : new InvoiceDeliveryView(
                delivery.Id,
                delivery.Status.ToString().ToLowerInvariant(),
                delivery.Attempts,
                delivery.ProviderMessageId,
                delivery.SettledAt));
}
