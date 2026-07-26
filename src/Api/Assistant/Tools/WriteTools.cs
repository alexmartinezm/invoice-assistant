using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Api.Domain;
using Api.Features.Invoices;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Assistant.Tools;

/// <summary>
/// The tools that can change something. Every one of them goes through <see cref="ToolGate"/>,
/// which decides whether the call executes, waits for a person, or is refused.
/// </summary>
/// <remarks>
/// Parameter names are snake_case for the same reason as the read tools: they are the public
/// contract that appears in the model's function schema and in the eval cases.
/// </remarks>
public sealed class WriteTools(ToolGate gate, AppDbContext db, IOptions<InvoicingOptions> invoicing)
{
    [Description("Creates a draft invoice for a customer. The invoice is not sent; it starts as a draft.")]
    public Task<JsonElement> CreateDraftInvoiceAsync(
        [Description("Full or partial customer name; the server resolves it and refuses if it is ambiguous.")]
        string customer_name,
        [Description("The invoice lines, each with a description, a quantity and a unit price.")]
        NewLine[] lines,
        CancellationToken cancellationToken = default) =>
        gate.RunAsync(
            WriteToolCatalog.CreateDraftInvoice,
            new { customer_name, lines },
            _ => Task.FromResult(
                $"Create a draft invoice for {customer_name} with {lines.Length} line(s), "
                + $"totalling {Money(lines.Sum(line => line.quantity * line.unit_price))} before tax"),
            WriteToolPlans.CreateDraftInvoice(
                customer_name,
                lines.Select(line => (object)new
                {
                    description = line.description,
                    quantity = line.quantity,
                    unitPrice = line.unit_price,
                })),
            cancellationToken);

    [Description("Sends a draft invoice to the customer, moving it from Draft to Sent.")]
    public Task<JsonElement> SendInvoiceAsync(
        [Description("Invoice number in yyyy-nnnn form, for example 2026-0041.")]
        string number,
        CancellationToken cancellationToken = default) =>
        gate.RunAsync(
            WriteToolCatalog.SendInvoice,
            new { number },
            token => DescribeInvoiceAsync("Send invoice", number, token),
            WriteToolPlans.SendInvoice(number),
            cancellationToken);

    [Description("Marks a sent invoice as paid.")]
    public Task<JsonElement> MarkInvoicePaidAsync(
        [Description("Invoice number in yyyy-nnnn form, for example 2026-0041.")]
        string number,
        CancellationToken cancellationToken = default) =>
        gate.RunAsync(
            WriteToolCatalog.MarkInvoicePaid,
            new { number },
            token => DescribeInvoiceAsync("Mark invoice", number, token, suffix: "as paid"),
            WriteToolPlans.MarkInvoicePaid(number),
            cancellationToken);

    [Description("Cancels a draft or sent invoice. This cannot be undone.")]
    public Task<JsonElement> CancelInvoiceAsync(
        [Description("Invoice number in yyyy-nnnn form, for example 2026-0041.")]
        string number,
        CancellationToken cancellationToken = default) =>
        gate.RunAsync(
            WriteToolCatalog.CancelInvoice,
            new { number },
            token => DescribeInvoiceAsync("Cancel invoice", number, token),
            WriteToolPlans.CancelInvoice(number),
            cancellationToken);

    [Description("Changes the due date of an invoice that is not yet paid or cancelled.")]
    public Task<JsonElement> UpdateDueDateAsync(
        [Description("Invoice number in yyyy-nnnn form, for example 2026-0041.")]
        string number,
        [Description("The new due date, as yyyy-MM-dd. It cannot precede the issue date.")]
        DateOnly new_due_date,
        CancellationToken cancellationToken = default) =>
        gate.RunAsync(
            WriteToolCatalog.UpdateDueDate,
            new { number, new_due_date },
            token => DescribeInvoiceAsync(
                "Change the due date of invoice", number, token, suffix: $"to {new_due_date:yyyy-MM-dd}"),
            WriteToolPlans.UpdateDueDate(number, new_due_date),
            cancellationToken);

    /// <summary>Resolves the invoice so the approval card names a customer and an amount, not just a number.</summary>
    private async Task<string> DescribeInvoiceAsync(
        string verb,
        string number,
        CancellationToken cancellationToken,
        string? suffix = null)
    {
        var invoice = await db.Invoices
            .AsNoTracking()
            .Where(i => i.Number == number)
            .Select(i => new { i.Number, Customer = i.Customer!.Name, i.Total })
            .SingleOrDefaultAsync(cancellationToken);

        var subject = invoice is null
            ? number
            : $"{invoice.Number} ({invoice.Customer}, {Money(invoice.Total)})";

        return suffix is null ? $"{verb} {subject}" : $"{verb} {subject} {suffix}";
    }

    private string Money(decimal amount) => string.Create(
        CultureInfo.GetCultureInfo(invoicing.Value.Locale),
        $"{amount:N2} {invoicing.Value.Currency}");

    /// <summary>One line of a proposed invoice, in the snake_case the tool schema exposes.</summary>
    public sealed record NewLine(string description, decimal quantity, decimal unit_price);
}

/// <summary>
/// The write tools' identities in one place, so the catalog and the gate agree without the gate
/// having to depend on the catalog that builds it.
/// </summary>
public static class WriteToolCatalog
{
    public static readonly ToolIdentity CreateDraftInvoice =
        new("create_draft_invoice", ToolSideEffect.Write, Role.Accountant);

    public static readonly ToolIdentity SendInvoice =
        new("send_invoice", ToolSideEffect.Write, Role.Accountant);

    public static readonly ToolIdentity MarkInvoicePaid =
        new("mark_invoice_paid", ToolSideEffect.Write, Role.Accountant);

    public static readonly ToolIdentity CancelInvoice =
        new("cancel_invoice", ToolSideEffect.Write, Role.Admin);

    public static readonly ToolIdentity UpdateDueDate =
        new("update_due_date", ToolSideEffect.Write, Role.Accountant);

    public static readonly IReadOnlyList<ToolIdentity> All =
    [
        CreateDraftInvoice, SendInvoice, MarkInvoicePaid, CancelInvoice, UpdateDueDate,
    ];
}
