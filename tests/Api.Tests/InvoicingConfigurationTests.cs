using System.Net;
using System.Net.Http.Json;
using Api.Assistant;
using Api.Features.Invoices;
using Api.Features.Reports;
using Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>
/// Boots the whole application for a different market. Currency, locale, tax label and rates are
/// configuration, and the only way to know that is true everywhere — API responses, the seeded
/// ledger, the assistant's own formatting instruction — is to run it and look.
/// </summary>
public sealed class UnitedStatesApiFactory : ApiFactory
{
    public const string Currency = "USD";
    public const decimal StandardRate = 0.07m;
    public const decimal ReducedRate = 0.02m;
    public const decimal SettlementLimit = 500m;

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["Invoicing:Currency"] = Currency,
            ["Invoicing:CurrencySymbol"] = "$",
            ["Invoicing:Locale"] = "en-US",
            ["Invoicing:TaxLabel"] = "Sales tax",
            ["Invoicing:DefaultVatRate"] = "0.07",
            ["Invoicing:ReducedVatRate"] = "0.02",
            ["Invoicing:AccountantMarkPaidLimit"] = "500",
        };
}

public class InvoicingConfigurationTests(UnitedStatesApiFactory factory)
    : IClassFixture<UnitedStatesApiFactory>
{
    [Fact]
    public async Task The_public_config_endpoint_reports_the_configured_market()
    {
        using var client = factory.CreateClient();

        // Anonymous on purpose: the login screen needs it before anyone has a token.
        var response = await client.GetAsync("/api/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var config = await response.Content.ReadFromJsonAsync<AppConfigPayload>();

        Assert.Equal(UnitedStatesApiFactory.Currency, config!.Currency);
        Assert.Equal("en-US", config.Locale);
        Assert.Equal("Sales tax", config.TaxLabel);
        Assert.Equal(UnitedStatesApiFactory.StandardRate, config.DefaultVatRate);
        Assert.Equal(UnitedStatesApiFactory.SettlementLimit, config.AccountantMarkPaidLimit);
    }

    [Fact]
    public async Task Business_responses_carry_the_configured_currency()
    {
        using var client = await factory.ClientForAsync("ana@demo");

        var invoices = await client.GetFromJsonAsync<InvoiceList>("/api/invoices");
        var receivables = await client.GetFromJsonAsync<ReceivablesReport>("/api/reports/receivables");

        Assert.Equal(UnitedStatesApiFactory.Currency, invoices!.Currency);
        Assert.Equal(UnitedStatesApiFactory.Currency, receivables!.Currency);
    }

    [Fact]
    public async Task The_seeded_ledger_uses_the_configured_tax_rates()
    {
        using var client = await factory.ClientForAsync("ana@demo");
        var invoices = await client.GetFromJsonAsync<InvoiceList>("/api/invoices?limit=100");

        var rates = new HashSet<decimal>();
        foreach (var summary in invoices!.Invoices.Take(15))
        {
            var detail = await client.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{summary.Number}");
            rates.Add(detail!.VatRate);
        }

        // 21% would mean the seeder kept its own constants instead of reading configuration.
        Assert.All(rates, rate =>
            Assert.True(
                rate == UnitedStatesApiFactory.StandardRate || rate == UnitedStatesApiFactory.ReducedRate,
                $"unexpected rate {rate}"));
    }

    [Fact]
    public async Task A_new_draft_gets_the_configured_default_rate()
    {
        using var client = await factory.ClientForAsync("carlos@demo");

        var response = await client.PostAsJsonAsync("/api/invoices", new
        {
            customerName = "Tarraco Software",
            lines = new[] { new { description = "Development", quantity = 1, unitPrice = 100m } },
        });
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<InvoiceDetail>();

        Assert.Equal(UnitedStatesApiFactory.StandardRate, created!.VatRate);
        Assert.Equal(7m, created.VatAmount);
        Assert.Equal(107m, created.Total);
    }

    [Fact]
    public async Task The_settlement_limit_is_the_configured_one_and_is_quoted_in_the_currency()
    {
        using var client = await factory.ClientForAsync("carlos@demo");

        var created = await client.PostAsJsonAsync("/api/invoices", new
        {
            customerName = "Delta Logística",
            lines = new[] { new { description = "Development", quantity = 1, unitPrice = 800m } },
        });
        created.EnsureSuccessStatusCode();
        var invoice = await created.Content.ReadFromJsonAsync<InvoiceDetail>();

        var sent = await client.PostAsync($"/api/invoices/{invoice!.Number}/send", content: null);
        sent.EnsureSuccessStatusCode();

        // 856.00 clears the default 1,000 ceiling but not the 500 configured here.
        var refused = await client.PostAsync($"/api/invoices/{invoice.Number}/mark-paid", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("amount_limit_exceeded", problem!.Code);
        Assert.Contains("500.00 USD", problem.Detail);
    }

    [Fact]
    public void The_system_prompt_is_rendered_for_the_configured_market()
    {
        using var scope = factory.Services.CreateScope();
        var prompt = scope.ServiceProvider.GetRequiredService<SystemPromptProvider>();

        Assert.Contains("Amounts in USD", prompt.Text);
        Assert.Contains("$1,240.50", prompt.Text);
        Assert.Contains("07/26/2026", prompt.Text);
        Assert.Contains("mm/dd/yyyy", prompt.Text);

        // No placeholder may survive into what the model is given.
        Assert.DoesNotContain("{{", prompt.Text);
        Assert.DoesNotContain("euros", prompt.Text);
        Assert.DoesNotContain("€", prompt.Text);
    }

    private sealed record AppConfigPayload(
        string Currency,
        string Locale,
        string TaxLabel,
        decimal DefaultVatRate,
        decimal AccountantMarkPaidLimit);

    private sealed record ProblemPayload(string? Code, string? Detail);
}
