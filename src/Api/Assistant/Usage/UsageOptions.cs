namespace Api.Assistant.Usage;

/// <summary>
/// Cost accounting configuration: what each model costs and how much the whole demo may spend per
/// day. Prices live here rather than in code so a price change is a config edit, not a release.
/// </summary>
public sealed class UsageOptions
{
    public const string SectionName = "Usage";

    /// <summary>
    /// Global daily spend cap in euros, across all users — the kill switch. A per-user rate limit
    /// does not protect against a scraper with many IPs; this does. Zero blocks every call.
    /// </summary>
    public decimal DailyBudgetEur { get; set; } = 1m;

    public List<ModelPrice> Prices { get; set; } = [];

    /// <summary>
    /// Longest matching prefix wins, so <c>gpt-4o-mini-2024-07-18</c> finds the <c>gpt-4o-mini</c>
    /// entry rather than <c>gpt-4o</c>. Null when no entry matches: the caller records the call at
    /// zero cost and says so, because silently guessing a price would corrupt the one number this
    /// table exists to get right.
    /// </summary>
    public ModelPrice? PriceFor(string model) => Prices
        .Where(price => model.StartsWith(price.Model, StringComparison.OrdinalIgnoreCase))
        .MaxBy(price => price.Model.Length);
}

/// <summary>Euros per million tokens, the unit providers publish their price lists in.</summary>
public sealed class ModelPrice
{
    public string Model { get; set; } = string.Empty;

    public decimal InputPer1MEur { get; set; }

    public decimal OutputPer1MEur { get; set; }

    public decimal CostFor(int promptTokens, int completionTokens) =>
        (promptTokens * InputPer1MEur + completionTokens * OutputPer1MEur) / 1_000_000m;
}
