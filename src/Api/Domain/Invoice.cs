using System.Globalization;

namespace Api.Domain;

/// <summary>
/// Invoice aggregate. Every state transition and every monetary calculation lives here, so the
/// rules hold whichever way the invoice is reached: REST endpoint, assistant tool or seed.
/// </summary>
public sealed class Invoice
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice() { }

    public Guid Id { get; private init; } = Guid.CreateVersion7();

    /// <summary>Human-facing identifier in <c>yyyy-nnnn</c> form, e.g. <c>2026-0001</c>.</summary>
    public string Number { get; private init; } = string.Empty;

    public Guid CustomerId { get; private init; }

    public Customer? Customer { get; private init; }

    public InvoiceStatus Status { get; private set; } = InvoiceStatus.Draft;

    public DateOnly IssueDate { get; private init; }

    public DateOnly DueDate { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public decimal Subtotal { get; private set; }

    public decimal VatRate { get; private init; }

    public decimal VatAmount { get; private set; }

    public decimal Total { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>
    /// How many times this invoice has been changed. Starts at 1 and is bumped by every mutation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the invoice's ETag, and the thing an approval is bound to. The assistant's approval
    /// window is five minutes, and "mark 2026-0041 as paid" agreed to five minutes ago is not the
    /// same instruction if somebody has cancelled the invoice in between — the arguments are
    /// identical and the situation is not. The approval carries the revision it was proposed
    /// against and the write fails closed on a mismatch.
    /// </para>
    /// <para>
    /// Application-managed rather than a database <c>xmin</c>: the value travels in an HTTP header
    /// and back into a stored approval, so it has to be a number this code owns and can reason
    /// about, not a provider detail. It is mapped as an EF concurrency token, which puts the
    /// original value into the <c>WHERE</c> clause of every update — so two writers racing on one
    /// invoice is a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> rather
    /// than a silent last-writer-wins.
    /// </para>
    /// </remarks>
    public long Revision { get; private set; } = 1;

    public static string FormatNumber(int year, int sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{sequence:0000}");

    public static Invoice CreateDraft(
        string number,
        Customer customer,
        DateOnly issueDate,
        DateOnly dueDate,
        decimal vatRate,
        IEnumerable<NewInvoiceLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        ArgumentNullException.ThrowIfNull(customer);

        if (dueDate < issueDate)
        {
            throw new DomainException(
                "invalid_due_date",
                $"The due date ({dueDate:yyyy-MM-dd}) cannot be earlier than the issue date ({issueDate:yyyy-MM-dd}).");
        }

        if (vatRate is < 0m or > 1m)
        {
            throw new DomainException("invalid_vat_rate", "The VAT rate must be a fraction between 0 and 1.");
        }

        var invoice = new Invoice
        {
            Number = number,
            CustomerId = customer.Id,
            IssueDate = issueDate,
            DueDate = dueDate,
            VatRate = vatRate,
        };

        foreach (var line in lines)
        {
            invoice.AddLine(line);
        }

        if (invoice._lines.Count == 0)
        {
            throw new DomainException("empty_invoice", "An invoice needs at least one line.");
        }

        invoice.Recalculate();
        return invoice;
    }

    /// <summary>
    /// An invoice is overdue when it was sent, is still unpaid and its due date has passed.
    /// Derived on read so it can never go stale in the database.
    /// </summary>
    public bool IsOverdue(DateOnly today) => Status is InvoiceStatus.Sent && DueDate < today;

    /// <summary>Days past the due date, or 0 while the invoice is still current.</summary>
    public int DaysOverdue(DateOnly today) =>
        IsOverdue(today) ? today.DayNumber - DueDate.DayNumber : 0;

    public void Send()
    {
        if (Status is not InvoiceStatus.Draft)
        {
            throw new DomainException(
                "invalid_transition",
                $"Only a draft invoice can be sent; {Number} is {Status}.");
        }

        Status = InvoiceStatus.Sent;
        Revision++;
    }

    public void MarkPaid(DateTimeOffset paidAt)
    {
        if (Status is not InvoiceStatus.Sent)
        {
            throw new DomainException(
                "invalid_transition",
                $"Only a sent invoice can be marked as paid; {Number} is {Status}.");
        }

        Status = InvoiceStatus.Paid;
        PaidAt = paidAt;
        Revision++;
    }

    public void Cancel()
    {
        if (Status is not (InvoiceStatus.Draft or InvoiceStatus.Sent))
        {
            throw new DomainException(
                "invalid_transition",
                $"Only a draft or sent invoice can be cancelled; {Number} is {Status}.");
        }

        Status = InvoiceStatus.Cancelled;
        Revision++;
    }

    public void ChangeDueDate(DateOnly newDueDate)
    {
        if (Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled)
        {
            throw new DomainException(
                "invalid_transition",
                $"The due date of a {Status} invoice cannot be changed; {Number} is closed.");
        }

        if (newDueDate < IssueDate)
        {
            throw new DomainException(
                "invalid_due_date",
                $"The due date ({newDueDate:yyyy-MM-dd}) cannot be earlier than the issue date ({IssueDate:yyyy-MM-dd}).");
        }

        DueDate = newDueDate;
        Revision++;
    }

    private void AddLine(NewInvoiceLine line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line.Description);

        if (line.Quantity <= 0m)
        {
            throw new DomainException("invalid_line", $"The quantity of '{line.Description}' must be greater than zero.");
        }

        if (line.UnitPrice < 0m)
        {
            throw new DomainException("invalid_line", $"The unit price of '{line.Description}' cannot be negative.");
        }

        _lines.Add(new InvoiceLine
        {
            InvoiceId = Id,
            Description = line.Description.Trim(),
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
        });
    }

    private void Recalculate()
    {
        foreach (var line in _lines)
        {
            line.Recalculate();
        }

        Subtotal = decimal.Round(_lines.Sum(l => l.Amount), 2, MidpointRounding.AwayFromZero);
        VatAmount = decimal.Round(Subtotal * VatRate, 2, MidpointRounding.AwayFromZero);
        Total = Subtotal + VatAmount;
    }
}

public readonly record struct NewInvoiceLine(string Description, decimal Quantity, decimal UnitPrice);
