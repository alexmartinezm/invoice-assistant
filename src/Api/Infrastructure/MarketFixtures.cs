using Api.Domain;

namespace Api.Infrastructure;

public sealed record SeedCustomer(string Name, string TaxId, string Email);

public sealed record SeedStaff(string Email, string DisplayName, Role Role);

/// <summary>
/// Who the demo's customers and staff are. This is deliberately a separate setting from
/// <c>Invoicing:Locale</c>: the locale decides how figures are formatted for whoever is reading,
/// the market decides whose ledger it is. A London controller looking at a Spanish subsidiary is a
/// real arrangement, so the two are allowed to differ.
/// </summary>
/// <remarks>
/// Company names and tax identifiers are data, not prose — they stay in the form their market
/// actually uses (SL/GmbH/Inc, B-prefixed NIF vs EIN vs VAT number). Everything the repo *writes* —
/// line item descriptions, comments, documentation — stays English.
/// </remarks>
public sealed record MarketFixtures(
    string Market,
    IReadOnlyList<SeedCustomer> Customers,
    IReadOnlyList<SeedStaff> Staff)
{
    /// <summary>Used when the configured market has no fixture set of its own.</summary>
    public const string FallbackMarket = "es-ES";

    public static IReadOnlyCollection<string> AvailableMarkets => All.Keys;

    /// <summary>
    /// Resolves the fixture set. An unknown market falls back rather than failing to boot — but it
    /// says so, because silently serving Spanish customers to someone who asked for Japan is the
    /// kind of thing nobody notices until a demo.
    /// </summary>
    public static MarketFixtures For(string? market, ILogger? logger = null)
    {
        var requested = string.IsNullOrWhiteSpace(market) ? FallbackMarket : market.Trim();

        if (All.TryGetValue(requested, out var exact))
        {
            return exact;
        }

        // "en" should find en-GB rather than dropping all the way to the fallback.
        var language = requested.Split('-')[0];
        var byLanguage = All.FirstOrDefault(entry =>
            entry.Key.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));

        if (byLanguage.Value is not null)
        {
            logger?.LogInformation(
                "No seed fixtures for market '{Requested}'; using '{Chosen}' (same language).",
                requested,
                byLanguage.Key);
            return byLanguage.Value;
        }

        logger?.LogWarning(
            "No seed fixtures for market '{Requested}'; falling back to '{Fallback}'. Available: {Available}.",
            requested,
            FallbackMarket,
            string.Join(", ", All.Keys));

        return All[FallbackMarket];
    }

    private static readonly Dictionary<string, MarketFixtures> All =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["es-ES"] = new(
                "es-ES",
                [
                    new("Acme Ibérica SL", "B12345678", "facturacion@acme-iberica.es"),
                    new("Norvento Energía SA", "A87654321", "cuentas@norvento.es"),
                    new("Delta Logística SL", "B23456789", "admin@deltalogistica.es"),
                    new("Estudio Marín Arquitectos", "B34567890", "hola@estudiomarin.com"),
                    new("Bodegas Ribalta SL", "B45678901", "pagos@bodegasribalta.es"),
                    new("Clínica Sant Jordi", "B56789012", "administracion@santjordi.cat"),
                    new("Tarraco Software SL", "B67890123", "finance@tarracosoftware.com"),
                    new("Hostelería del Puerto SA", "A78901234", "contabilidad@hosteleriapuerto.es"),
                ],
                [
                    new("ana@demo", "Ana Ferrer", Role.Admin),
                    new("carlos@demo", "Carlos Ibáñez", Role.Accountant),
                    new("lucia@demo", "Lucía Prat", Role.Viewer),
                ]),

            ["en-US"] = new(
                "en-US",
                [
                    new("Northwind Traders Inc", "41-2039571", "billing@northwindtraders.com"),
                    new("Cascade Robotics LLC", "87-1120394", "ap@cascaderobotics.com"),
                    new("Harborview Logistics Inc", "26-4471902", "accounts@harborviewlogistics.com"),
                    new("Pinehurst Architects LLC", "33-8890124", "hello@pinehurstarchitects.com"),
                    new("Sierra Vineyards Inc", "45-2210987", "payables@sierravineyards.com"),
                    new("Lakeside Medical Group", "52-7761203", "admin@lakesidemedical.com"),
                    new("Fremont Software Inc", "68-3345012", "finance@fremontsoftware.com"),
                    new("Bayside Hospitality LLC", "71-9902334", "accounting@baysidehospitality.com"),
                ],
                [
                    new("ana@demo", "Anna Fisher", Role.Admin),
                    new("carlos@demo", "Carl Iverson", Role.Accountant),
                    new("lucia@demo", "Lucy Pratt", Role.Viewer),
                ]),

            ["en-GB"] = new(
                "en-GB",
                [
                    new("Acme Britannia Ltd", "GB123456789", "billing@acmebritannia.co.uk"),
                    new("Northwind Energy plc", "GB987654321", "accounts@northwindenergy.co.uk"),
                    new("Delta Freight Ltd", "GB234567891", "admin@deltafreight.co.uk"),
                    new("Marlow Architects Ltd", "GB345678912", "hello@marlowarchitects.co.uk"),
                    new("Ridgeway Vintners Ltd", "GB456789123", "payables@ridgewayvintners.co.uk"),
                    new("St Georges Clinic Ltd", "GB567891234", "administration@stgeorgesclinic.co.uk"),
                    new("Tarrant Software Ltd", "GB678912345", "finance@tarrantsoftware.co.uk"),
                    new("Harbour Hospitality plc", "GB789123456", "accounting@harbourhospitality.co.uk"),
                ],
                [
                    new("ana@demo", "Anna Fielding", Role.Admin),
                    new("carlos@demo", "Charles Ingram", Role.Accountant),
                    new("lucia@demo", "Lucy Prentice", Role.Viewer),
                ]),

            ["de-DE"] = new(
                "de-DE",
                [
                    new("Acme Deutschland GmbH", "DE123456789", "rechnung@acme-deutschland.de"),
                    new("Nordwind Energie AG", "DE987654321", "buchhaltung@nordwind-energie.de"),
                    new("Delta Logistik GmbH", "DE234567891", "verwaltung@delta-logistik.de"),
                    new("Architekturbüro Marin GmbH", "DE345678912", "hallo@marin-architektur.de"),
                    new("Weingut Ribalta GmbH", "DE456789123", "zahlungen@weingut-ribalta.de"),
                    new("Klinik Sankt Georg GmbH", "DE567891234", "verwaltung@klinik-sanktgeorg.de"),
                    new("Tarraco Software GmbH", "DE678912345", "finanzen@tarraco-software.de"),
                    new("Hafen Gastronomie AG", "DE789123456", "buchhaltung@hafen-gastronomie.de"),
                ],
                [
                    new("ana@demo", "Anja Fischer", Role.Admin),
                    new("carlos@demo", "Karl Iversen", Role.Accountant),
                    new("lucia@demo", "Lena Prenzel", Role.Viewer),
                ]),
        };
}
