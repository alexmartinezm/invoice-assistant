using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Assistant.Usage;

public sealed record BudgetStatus(decimal SpentTodayEur, decimal DailyBudgetEur)
{
    public bool Exhausted => SpentTodayEur >= DailyBudgetEur;
}

/// <summary>
/// Today's spend against the daily cap. The number comes from the database, not from a counter in
/// memory, so it survives restarts and is the same figure the usage endpoints report.
/// </summary>
public sealed class UsageBudget(AppDbContext db, IClock clock, IOptions<UsageOptions> options)
{
    public async Task<BudgetStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        // The budget day is the UTC day. A demo does not need a billing timezone; it needs the
        // same boundary everywhere it is checked.
        var startOfDay = new DateTimeOffset(clock.UtcNow.UtcDateTime.Date, TimeSpan.Zero);

        var spent = await db.UsageRecords
            .Where(record => record.CreatedAt >= startOfDay)
            .SumAsync(record => (decimal?)record.CostEur, cancellationToken) ?? 0m;

        return new BudgetStatus(spent, options.Value.DailyBudgetEur);
    }
}

/// <summary>
/// Raised instead of calling the model once the day's budget is spent. <c>/api/chat</c> turns it
/// into a 429 before the stream starts and into an <c>error</c> event after.
/// </summary>
public sealed class DailyBudgetExhaustedException(BudgetStatus status)
    : Exception($"The daily assistant budget of {status.DailyBudgetEur:0.00}€ is spent "
        + $"({status.SpentTodayEur:0.0000}€ used today). It resets at midnight UTC.")
{
    public BudgetStatus Status { get; } = status;
}
