namespace Api.Features.Invoices;

public sealed class InvoicingOptions
{
    public const string SectionName = "Invoicing";

    public string Currency { get; set; } = "EUR";

    public decimal DefaultVatRate { get; set; } = 0.21m;

    public int DefaultPaymentTermDays { get; set; } = 30;

    /// <summary>
    /// An Accountant may settle invoices up to this amount; anything larger needs an Admin.
    /// This is API authorization, independent of the assistant's policy gate: calling the
    /// endpoint directly is subject to the same limit.
    /// </summary>
    public decimal AccountantMarkPaidLimit { get; set; } = 1_000m;

    public int DefaultPageSize { get; set; } = 50;

    public int MaxPageSize { get; set; } = 100;
}
