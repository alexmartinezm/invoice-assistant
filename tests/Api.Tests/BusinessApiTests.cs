using System.Net;
using System.Net.Http.Json;
using Api.Features.Invoices;
using Api.Features.Reports;
using Api.Tests.Support;

namespace Api.Tests;

/// <summary>
/// The business API stands on its own: whoever calls it — the browser, curl or the assistant —
/// meets the same authorization and the same domain rules.
/// </summary>
public class BusinessApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task An_anonymous_caller_gets_nothing()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_create_an_invoice()
    {
        using var client = await factory.ClientForAsync("lucia@demo");

        var response = await client.PostAsJsonAsync("/api/invoices", new
        {
            customerName = "Acme",
            lines = new[] { new { description = "Development", quantity = 1, unitPrice = 100 } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_read_everything()
    {
        using var client = await factory.ClientForAsync("lucia@demo");

        var invoices = await client.GetFromJsonAsync<InvoiceList>("/api/invoices");

        Assert.NotNull(invoices);
        Assert.NotEmpty(invoices.Invoices);
    }

    [Fact]
    public async Task An_accountant_can_settle_a_small_invoice_but_not_a_large_one()
    {
        using var accountant = await factory.ClientForAsync("carlos@demo");

        var small = await CreateAndSendAsync(accountant, unitPrice: 100m);
        var settled = await accountant.PostAsync($"/api/invoices/{small}/mark-paid", content: null);
        Assert.Equal(HttpStatusCode.OK, settled.StatusCode);

        var large = await CreateAndSendAsync(accountant, unitPrice: 5_000m);
        var refused = await accountant.PostAsync($"/api/invoices/{large}/mark-paid", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("amount_limit_exceeded", problem!.Code);
    }

    [Fact]
    public async Task Only_an_admin_can_cancel()
    {
        using var accountant = await factory.ClientForAsync("carlos@demo");
        using var admin = await factory.ClientForAsync("ana@demo");
        var number = await CreateAndSendAsync(accountant, unitPrice: 200m);

        var refused = await accountant.PostAsync($"/api/invoices/{number}/cancel", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var accepted = await admin.PostAsync($"/api/invoices/{number}/cancel", content: null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var cancelled = await accepted.Content.ReadFromJsonAsync<InvoiceDetail>();
        Assert.Equal("Cancelled", cancelled!.Status);
    }

    [Fact]
    public async Task An_invalid_transition_comes_back_as_a_readable_domain_error()
    {
        using var admin = await factory.ClientForAsync("ana@demo");
        var drafts = await admin.GetFromJsonAsync<InvoiceList>("/api/invoices?status=Draft");
        var draft = drafts!.Invoices[0];

        var response = await admin.PostAsync($"/api/invoices/{draft.Number}/mark-paid", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("invalid_transition", problem!.Code);
        Assert.Contains("Only a sent invoice", problem.Detail);
    }

    [Fact]
    public async Task Overdue_is_derived_from_the_due_date_and_is_not_a_status()
    {
        using var admin = await factory.ClientForAsync("ana@demo");

        var overdue = await admin.GetFromJsonAsync<InvoiceList>("/api/invoices?overdue=true&limit=100");
        Assert.All(overdue!.Invoices, invoice =>
        {
            Assert.Equal("Sent", invoice.Status);
            Assert.True(invoice.IsOverdue);
            Assert.True(invoice.DueDate < overdue.AsOf);
        });

        var rejected = await admin.GetAsync("/api/invoices?status=Overdue");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    /// <summary>
    /// A page says how much it left behind. The assistant relays this payload as-is, so a list
    /// whose only number is "rows in this response" is a wrong answer to "how many are overdue?"
    /// waiting to be stated as a fact.
    /// </summary>
    [Fact]
    public async Task A_truncated_list_reports_the_total_it_matched()
    {
        using var admin = await factory.ClientForAsync("ana@demo");

        var everything = await admin.GetFromJsonAsync<InvoiceList>("/api/invoices?limit=100");
        Assert.NotNull(everything);
        Assert.False(everything.Truncated);
        Assert.Equal(everything.Count, everything.Total);
        Assert.Equal(everything.Invoices.Count, everything.Count);

        var page = await admin.GetFromJsonAsync<InvoiceList>("/api/invoices?limit=3");
        Assert.NotNull(page);
        Assert.Equal(3, page.Count);
        Assert.True(page.Truncated);

        // The total is the whole match, not the page: the same number the unpaged call reported.
        Assert.Equal(everything.Total, page.Total);

        // And it still tracks the filter rather than the table.
        var overdue = await admin.GetFromJsonAsync<InvoiceList>("/api/invoices?overdue=true&limit=1");
        Assert.NotNull(overdue);
        Assert.True(overdue.Total < everything.Total);
        Assert.Equal(overdue.Total > 1, overdue.Truncated);
    }

    [Fact]
    public async Task The_aging_buckets_add_up_to_the_outstanding_total()
    {
        using var admin = await factory.ClientForAsync("ana@demo");

        var report = await admin.GetFromJsonAsync<ReceivablesReport>("/api/reports/receivables");

        Assert.NotNull(report);
        Assert.Equal(4, report.Buckets.Count);
        Assert.Equal(report.TotalOutstanding, report.Buckets.Sum(b => b.Amount));
        Assert.Equal(report.InvoiceCount, report.Buckets.Sum(b => b.InvoiceCount));
        Assert.Equal(
            report.TotalOverdue,
            report.Buckets.Where(b => b.Key != "current").Sum(b => b.Amount));

        // The seed is built so the demo always has something in every bucket.
        Assert.All(report.Buckets, bucket => Assert.True(bucket.InvoiceCount > 0, $"bucket '{bucket.Key}' is empty"));
    }

    [Fact]
    public async Task A_created_draft_is_numbered_and_totalled_by_the_server()
    {
        using var accountant = await factory.ClientForAsync("carlos@demo");

        var response = await accountant.PostAsJsonAsync("/api/invoices", new
        {
            customerName = "Tarraco Software",
            lines = new[]
            {
                new { description = "Development", quantity = 10, unitPrice = 78m },
                new { description = "Support", quantity = 1, unitPrice = 380m },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<InvoiceDetail>();

        Assert.StartsWith("2026-", created!.Number);
        Assert.Equal("Draft", created.Status);
        Assert.Equal(1_160m, created.Subtotal);
        Assert.Equal(243.60m, created.VatAmount);
        Assert.Equal(1_403.60m, created.Total);
    }

    [Fact]
    public async Task An_ambiguous_customer_name_is_refused_rather_than_guessed()
    {
        using var accountant = await factory.ClientForAsync("carlos@demo");

        var response = await accountant.PostAsJsonAsync("/api/invoices", new
        {
            customerName = "S",
            lines = new[] { new { description = "Development", quantity = 1, unitPrice = 100 } },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("ambiguous_customer", problem!.Code);
    }

    /// <summary>
    /// Tests that change state work on invoices they created, so they never move the ground under
    /// a sibling test reading the seeded ledger.
    /// </summary>
    private static async Task<string> CreateAndSendAsync(HttpClient client, decimal unitPrice)
    {
        var created = await client.PostAsJsonAsync("/api/invoices", new
        {
            customerName = "Delta Logística",
            lines = new[] { new { description = "Development", quantity = 1, unitPrice } },
        });
        created.EnsureSuccessStatusCode();

        var invoice = await created.Content.ReadFromJsonAsync<InvoiceDetail>();
        var sent = await client.PostAsync($"/api/invoices/{invoice!.Number}/send", content: null);
        sent.EnsureSuccessStatusCode();

        return invoice.Number;
    }

    private sealed record ProblemPayload(string? Code, string? Detail);
}
