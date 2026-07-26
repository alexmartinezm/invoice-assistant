using Api.Assistant.Usage;

namespace Api.Tests;

/// <summary>Price resolution and cost arithmetic, the two things a wrong euro would trace back to.</summary>
public class UsageOptionsTests
{
    private static readonly UsageOptions Options = new()
    {
        Prices =
        [
            new ModelPrice { Model = "gpt-4o", InputPer1MEur = 2.3m, OutputPer1MEur = 9.2m },
            new ModelPrice { Model = "gpt-4o-mini", InputPer1MEur = 0.14m, OutputPer1MEur = 0.55m },
        ],
    };

    [Fact]
    public void The_longest_matching_prefix_wins()
    {
        // A dated deployment id resolves to its family, and the mini does not fall back to the
        // full-size price just because "gpt-4o" also matches.
        Assert.Equal("gpt-4o-mini", Options.PriceFor("gpt-4o-mini-2024-07-18")!.Model);
        Assert.Equal("gpt-4o", Options.PriceFor("gpt-4o-2024-11-20")!.Model);
    }

    [Fact]
    public void Matching_ignores_case_because_azure_deployment_names_do()
    {
        Assert.Equal("gpt-4o-mini", Options.PriceFor("GPT-4o-Mini")!.Model);
    }

    [Fact]
    public void An_unknown_model_has_no_price_rather_than_a_guessed_one()
    {
        Assert.Null(Options.PriceFor("claude-sonnet"));
        Assert.Null(Options.PriceFor(""));
    }

    [Fact]
    public void Cost_is_tokens_times_price_per_million()
    {
        var price = new ModelPrice { Model = "m", InputPer1MEur = 10m, OutputPer1MEur = 20m };

        Assert.Equal(0.0021m, price.CostFor(120, 45));
        Assert.Equal(0m, price.CostFor(0, 0));
        Assert.Equal(30m, price.CostFor(1_000_000, 1_000_000));
    }
}
