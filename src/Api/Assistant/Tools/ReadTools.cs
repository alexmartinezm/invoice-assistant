using System.ComponentModel;
using System.Text.Json;

namespace Api.Assistant.Tools;

/// <summary>
/// The assistant's read catalog. Every method returns the business API's JSON untouched, so what
/// the model reasons over is exactly what the endpoint authorised for this user.
/// </summary>
/// <remarks>
/// <para>
/// Parameter names are snake_case on purpose: they are the tool's public contract. They appear in
/// the function schema sent to the model and in the eval cases under <c>evals/cases/</c>, so
/// renaming one is a breaking change, not a style choice.
/// </para>
/// <para>
/// Optional parameters carry a default so the generated schema marks them optional. Without it the
/// model has to send every filter on every call, and omitting one fails argument binding.
/// </para>
/// <para>
/// Reads go through <see cref="ToolGate"/> as well. Nothing here changes anything, but the gate is
/// what counts calls against the per-turn ceiling, and it is what makes <c>defaults.read</c> in
/// <c>policies.json</c> a real setting rather than decoration.
/// </para>
/// </remarks>
public sealed class ReadTools(ToolGate gate)
{
    [Description(
        "Lists invoices with optional filters. Use overdue_only to find invoices past their due date. "
        + "The from/to filters bound the due date. The response is a page: 'count' is how many rows it "
        + "carries and 'total' is how many match the filters, so quote 'total' when asked how many there "
        + "are, and say the list is partial when 'truncated' is true. Never add up the returned amounts "
        + "yourself: use get_receivables_summary for totals.")]
    public Task<JsonElement> ListInvoicesAsync(
        [Description("Persisted status: Draft, Sent, Paid or Cancelled. 'Overdue' is not a status; use overdue_only.")]
        string? status = null,
        [Description("Full or partial customer name.")]
        string? customer_name = null,
        [Description("True to return only invoices that were sent and are past their due date.")]
        bool? overdue_only = null,
        [Description("Earliest due date, as yyyy-MM-dd.")]
        DateOnly? from = null,
        [Description("Latest due date, as yyyy-MM-dd.")]
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var query = new ToolQuery()
            .Add("status", status)
            .Add("customerName", customer_name)
            .Add("overdue", overdue_only)
            .Add("from", from)
            .Add("to", to);

        return Read(
            ReadToolCatalog.ListInvoices,
            new { status, customer_name, overdue_only, from, to },
            "List invoices",
            $"/api/invoices{query}",
            cancellationToken);
    }

    [Description("Returns one invoice in full, including its lines and totals.")]
    public Task<JsonElement> GetInvoiceAsync(
        [Description("Invoice number in yyyy-nnnn form, for example 2026-0041.")]
        string number,
        CancellationToken cancellationToken = default) =>
        Read(
            ReadToolCatalog.GetInvoice,
            new { number },
            $"Open invoice {number}",
            $"/api/invoices/{Uri.EscapeDataString(number)}",
            cancellationToken);

    [Description("Searches customers by name, tax id or email.")]
    public Task<JsonElement> SearchCustomersAsync(
        [Description("Search text; matches partially and ignores case.")]
        string query,
        CancellationToken cancellationToken = default) =>
        Read(
            ReadToolCatalog.SearchCustomers,
            new { query },
            $"Search customers for '{query}'",
            $"/api/customers{new ToolQuery().Add("query", query)}",
            cancellationToken);

    [Description(
        "Returns outstanding receivables broken down into aging buckets (current, 1-30, 31-60, over 60 days), "
        + "already totalled by the server. Use this for any question about how much is owed.")]
    public Task<JsonElement> GetReceivablesSummaryAsync(CancellationToken cancellationToken = default) =>
        Read(
            ReadToolCatalog.GetReceivablesSummary,
            new { },
            "Read the receivables summary",
            "/api/reports/receivables",
            cancellationToken);

    private Task<JsonElement> Read(
        ToolIdentity tool,
        object arguments,
        string summary,
        string url,
        CancellationToken cancellationToken) =>
        gate.RunAsync(
            tool,
            arguments,
            _ => Task.FromResult(summary),
            new ToolCall(HttpMethod.Get, url),
            cancellationToken);

    /// <summary>Builds a query string, skipping the parameters the model chose not to send.</summary>
    private sealed class ToolQuery
    {
        private readonly List<string> _parts = [];

        public ToolQuery Add(string name, string? value) =>
            string.IsNullOrWhiteSpace(value) ? this : Append(name, value.Trim());

        public ToolQuery Add(string name, bool? value) =>
            value is null ? this : Append(name, value.Value ? "true" : "false");

        public ToolQuery Add(string name, DateOnly? value) =>
            value is null ? this : Append(name, value.Value.ToString("yyyy-MM-dd"));

        public override string ToString() => _parts.Count == 0 ? string.Empty : $"?{string.Join('&', _parts)}";

        private ToolQuery Append(string name, string value)
        {
            _parts.Add($"{name}={Uri.EscapeDataString(value)}");
            return this;
        }
    }
}

/// <summary>
/// The read tools' identities, alongside <see cref="WriteToolCatalog"/> and for the same reason:
/// the gate has to know what it is evaluating without depending on the catalog that builds it.
/// </summary>
public static class ReadToolCatalog
{
    public static readonly ToolIdentity ListInvoices =
        new("list_invoices", ToolSideEffect.Read, Domain.Role.Viewer);

    public static readonly ToolIdentity GetInvoice =
        new("get_invoice", ToolSideEffect.Read, Domain.Role.Viewer);

    public static readonly ToolIdentity SearchCustomers =
        new("search_customers", ToolSideEffect.Read, Domain.Role.Viewer);

    public static readonly ToolIdentity GetReceivablesSummary =
        new("get_receivables_summary", ToolSideEffect.Read, Domain.Role.Viewer);

    public static readonly IReadOnlyList<ToolIdentity> All =
    [
        ListInvoices, GetInvoice, SearchCustomers, GetReceivablesSummary,
    ];
}
