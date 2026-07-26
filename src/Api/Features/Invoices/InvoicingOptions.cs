namespace Api.Features.Invoices;

/// <summary>
/// The one place the money rules are set. Everything downstream — the API responses, the seed, the
/// assistant's formatting instruction and the SPA's number formatting — reads from here, so a
/// deployment for a different market is a configuration change rather than a code change.
/// </summary>
public sealed class InvoicingOptions
{
    public const string SectionName = "Invoicing";

    /// <summary>ISO 4217 code carried on every list and report response, e.g. <c>EUR</c>, <c>USD</c>.</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// Symbol used only in the example inside the assistant's formatting instruction. The SPA gets
    /// the symbol from the browser's own currency data, so it never needs this.
    /// </summary>
    public string CurrencySymbol { get; set; } = "€";

    /// <summary>
    /// BCP 47 tag deciding how numbers and dates read — <c>en-GB</c> gives 1,240.50 and 26/07/2026,
    /// <c>de-DE</c> gives 1.240,50 and 26.07.2026.
    /// </summary>
    public string Locale { get; set; } = "en-GB";

    /// <summary>
    /// Which seed fixtures to use — whose customers and staff these are. Separate from
    /// <see cref="Locale"/> on purpose: the locale formats figures for the reader, the market
    /// decides whose ledger it is, and the two legitimately differ. Unknown values fall back with
    /// a warning; see <c>MarketFixtures</c> for the available sets.
    /// </summary>
    public string Market { get; set; } = "es-ES";

    /// <summary>What the tax line is called on screen — VAT, IVA, Sales tax, GST.</summary>
    public string TaxLabel { get; set; } = "VAT";

    /// <summary>Applied to a new invoice when the caller does not specify a rate.</summary>
    public decimal DefaultVatRate { get; set; } = 0.21m;

    /// <summary>The lower rate the seed sprinkles through the demo ledger, so not every invoice looks alike.</summary>
    public decimal ReducedVatRate { get; set; } = 0.10m;

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
