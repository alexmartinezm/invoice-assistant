using Api.Features.Invoices;
using Microsoft.Extensions.Options;

namespace Api.Features.Config;

/// <summary>
/// The display settings the SPA needs before it has any data — or any session. Money and dates are
/// formatted identically in the tables and in the assistant's answers only if both sides read the
/// same configuration, so the browser asks rather than hardcoding a second copy.
/// </summary>
public sealed record AppConfig(
    string Currency,
    string Locale,
    string TaxLabel,
    decimal DefaultVatRate,
    decimal AccountantMarkPaidLimit);

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder routes)
    {
        // Anonymous: the login screen quotes the Accountant's settlement limit, and it renders
        // before anyone has a token. Nothing here is a secret.
        routes.MapGet("/api/config", (IOptions<InvoicingOptions> options) =>
            {
                var invoicing = options.Value;

                return Results.Ok(new AppConfig(
                    invoicing.Currency,
                    invoicing.Locale,
                    invoicing.TaxLabel,
                    invoicing.DefaultVatRate,
                    invoicing.AccountantMarkPaidLimit));
            })
            .WithTags("Config")
            .AllowAnonymous();

        return routes;
    }
}
