using Api.Infrastructure;

namespace Api.Tests;

/// <summary>
/// Fixture selection, with no host and no database. The interesting behaviour is what happens for
/// a market nobody wrote fixtures for — silently serving Spanish customers to someone who asked
/// for Japan is exactly the sort of thing that surfaces during a live demo.
/// </summary>
public class MarketFixtureTests
{
    [Theory]
    [InlineData("es-ES")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    public void A_known_market_gets_its_own_fixtures(string market)
    {
        var fixtures = MarketFixtures.For(market);

        Assert.Equal(market, fixtures.Market);
        Assert.Equal(8, fixtures.Customers.Count);
        Assert.Equal(3, fixtures.Staff.Count);
    }

    [Fact]
    public void An_unknown_market_falls_back_to_the_documented_default()
    {
        var fixtures = MarketFixtures.For("ja-JP");

        Assert.Equal(MarketFixtures.FallbackMarket, fixtures.Market);
    }

    [Fact]
    public void An_unknown_region_prefers_the_same_language()
    {
        // en-AU has no fixtures of its own, but English ones are closer than Spanish ones.
        var fixtures = MarketFixtures.For("en-AU");

        Assert.StartsWith("en-", fixtures.Market);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_market_is_the_default_rather_than_an_error(string? market)
    {
        var fixtures = MarketFixtures.For(market);

        Assert.Equal(MarketFixtures.FallbackMarket, fixtures.Market);
    }

    [Fact]
    public void Every_market_keeps_the_same_three_demo_addresses()
    {
        // The addresses are named in the README, the eval cases and the tests; only the display
        // names are allowed to vary by market.
        foreach (var market in MarketFixtures.AvailableMarkets)
        {
            var fixtures = MarketFixtures.For(market);

            Assert.Equal(
                ["ana@demo", "carlos@demo", "lucia@demo"],
                fixtures.Staff.Select(s => s.Email).OrderBy(e => e));
        }
    }

    [Fact]
    public void Every_market_has_distinct_customer_names_and_tax_ids()
    {
        foreach (var market in MarketFixtures.AvailableMarkets)
        {
            var fixtures = MarketFixtures.For(market);

            Assert.Equal(fixtures.Customers.Count, fixtures.Customers.Select(c => c.Name).Distinct().Count());
            Assert.Equal(fixtures.Customers.Count, fixtures.Customers.Select(c => c.TaxId).Distinct().Count());
            Assert.All(fixtures.Customers, customer => Assert.Contains('@', customer.Email));
        }
    }
}
