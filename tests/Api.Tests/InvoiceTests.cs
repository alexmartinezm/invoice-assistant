using Api.Domain;

namespace Api.Tests;

/// <summary>
/// The state machine and the arithmetic, with no host and no database. These rules are the reason
/// the assistant cannot talk an invoice into an impossible state.
/// </summary>
public class InvoiceTests
{
    private static readonly DateOnly Issue = new(2026, 6, 1);
    private static readonly DateOnly Due = new(2026, 7, 1);

    [Fact]
    public void Totals_are_computed_from_the_lines()
    {
        var invoice = Draft(new NewInvoiceLine("Development", 10m, 78m), new NewInvoiceLine("Support", 1m, 380m));

        Assert.Equal(1_160m, invoice.Subtotal);
        Assert.Equal(243.60m, invoice.VatAmount);
        Assert.Equal(1_403.60m, invoice.Total);
        Assert.Equal([780m, 380m], invoice.Lines.Select(l => l.Amount));
    }

    [Fact]
    public void A_draft_can_be_sent_and_then_paid()
    {
        var invoice = Draft();

        invoice.Send();
        Assert.Equal(InvoiceStatus.Sent, invoice.Status);

        var paidAt = new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero);
        invoice.MarkPaid(paidAt);

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(paidAt, invoice.PaidAt);
    }

    [Fact]
    public void A_draft_cannot_be_marked_paid()
    {
        var invoice = Draft();

        var error = Assert.Throws<DomainException>(() => invoice.MarkPaid(DateTimeOffset.UtcNow));

        Assert.Equal("invalid_transition", error.Code);
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
    }

    [Fact]
    public void A_paid_invoice_cannot_be_cancelled()
    {
        var invoice = Draft();
        invoice.Send();
        invoice.MarkPaid(DateTimeOffset.UtcNow);

        var error = Assert.Throws<DomainException>(invoice.Cancel);

        Assert.Equal("invalid_transition", error.Code);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }

    [Fact]
    public void A_closed_invoice_keeps_its_due_date()
    {
        var invoice = Draft();
        invoice.Send();
        invoice.Cancel();

        var error = Assert.Throws<DomainException>(() => invoice.ChangeDueDate(Due.AddDays(30)));

        Assert.Equal("invalid_transition", error.Code);
        Assert.Equal(Due, invoice.DueDate);
    }

    [Fact]
    public void The_due_date_cannot_precede_the_issue_date()
    {
        var error = Assert.Throws<DomainException>(() => Invoice.CreateDraft(
            "2026-0001",
            Customer(),
            Issue,
            Issue.AddDays(-1),
            0.21m,
            [new NewInvoiceLine("Development", 1m, 100m)]));

        Assert.Equal("invalid_due_date", error.Code);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, false)]
    [InlineData(InvoiceStatus.Sent, true)]
    [InlineData(InvoiceStatus.Paid, false)]
    [InlineData(InvoiceStatus.Cancelled, false)]
    public void Only_a_sent_invoice_past_its_due_date_is_overdue(InvoiceStatus status, bool expected)
    {
        var invoice = Draft();

        if (status is not InvoiceStatus.Draft)
        {
            invoice.Send();
        }

        switch (status)
        {
            case InvoiceStatus.Paid:
                invoice.MarkPaid(DateTimeOffset.UtcNow);
                break;
            case InvoiceStatus.Cancelled:
                invoice.Cancel();
                break;
        }

        var today = Due.AddDays(15);

        Assert.Equal(expected, invoice.IsOverdue(today));
        Assert.Equal(expected ? 15 : 0, invoice.DaysOverdue(today));
    }

    [Fact]
    public void An_invoice_needs_at_least_one_line()
    {
        var error = Assert.Throws<DomainException>(() =>
            Invoice.CreateDraft("2026-0001", Customer(), Issue, Due, 0.21m, []));

        Assert.Equal("empty_invoice", error.Code);
    }

    [Fact]
    public void A_line_needs_a_positive_quantity()
    {
        var error = Assert.Throws<DomainException>(() => Draft(new NewInvoiceLine("Development", 0m, 100m)));

        Assert.Equal("invalid_line", error.Code);
    }

    private static Invoice Draft(params NewInvoiceLine[] lines) => Invoice.CreateDraft(
        "2026-0001",
        Customer(),
        Issue,
        Due,
        0.21m,
        lines.Length > 0 ? lines : [new NewInvoiceLine("Development", 1m, 100m)]);

    private static Customer Customer() => new()
    {
        Name = "Acme Ibérica SL",
        TaxId = "B12345678",
        Email = "facturacion@acme-iberica.es",
    };
}
