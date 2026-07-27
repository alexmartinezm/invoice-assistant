using Api.Domain;
using Api.Features.Invoices;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Features.Reports;

public sealed record AgingBucket(string Key, string Label, int InvoiceCount, decimal Amount);

/// <summary>What one customer owes, so ranking them is a lookup rather than a scan.</summary>
public sealed record CustomerReceivable(
    string CustomerName,
    int InvoiceCount,
    decimal Outstanding,
    decimal Overdue);

public sealed record ReceivablesReport(
    DateOnly AsOf,
    string Currency,
    decimal TotalOutstanding,
    decimal TotalOverdue,
    int InvoiceCount,
    IReadOnlyList<AgingBucket> Buckets,
    IReadOnlyList<CustomerReceivable> TopDebtors,
    int CustomerCount);

/// <summary>
/// The aging report exists so the model never has to add anything up. "How much are we owed?"
/// has to end in this endpoint; the calculation eval category guards that.
/// </summary>
/// <remarks>
/// The customer ranking is here for the same reason, and it was added because its absence produced
/// the worst failure this app can have: asked who owed the most, the assistant had a totals tool
/// with no customer dimension and a list tool it is told never to total, so it enumerated customers
/// one call at a time, hit the per-turn ceiling, and answered from the handful it had reached — a
/// confident number drawn from a partial scan. Aggregation the model needs has to exist on the
/// server, or the model will improvise it.
/// </remarks>
public static class ReportEndpoints
{
    /// <summary>
    /// How many customers the ranking names. Bounded so the payload does not grow with the ledger:
    /// the question this answers is "who owes the most", and the top of a sorted list answers it
    /// whatever the cap. <see cref="ReceivablesReport.CustomerCount"/> is what says the list is
    /// partial, the same contract the invoice list uses for its own truncation.
    /// </summary>
    private const int TopDebtorsLimit = 10;

    private sealed record Outstanding(Guid CustomerId, string CustomerName, DateOnly DueDate, decimal Total);

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGroup("/api/reports")
            .WithTags("Reports")
            .RequireAuthorization()
            .MapGet("/receivables", ReceivablesAsync);

        return routes;
    }

    private static async Task<IResult> ReceivablesAsync(
        AppDbContext db,
        IClock clock,
        IOptions<InvoicingOptions> options,
        CancellationToken cancellationToken)
    {
        var today = clock.Today;

        // Receivables are what has been billed and not yet collected: a draft is not owed yet and
        // a cancelled invoice never will be.
        var outstanding = await db.Invoices
            .AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Sent)
            .Select(i => new Outstanding(i.CustomerId, i.Customer!.Name, i.DueDate, i.Total))
            .ToListAsync(cancellationToken);

        List<AgingBucket> buckets =
        [
            BuildBucket("current", "Current", outstanding, today, int.MinValue, 0),
            BuildBucket("1-30", "1-30 days", outstanding, today, 1, 30),
            BuildBucket("31-60", "31-60 days", outstanding, today, 31, 60),
            BuildBucket("60+", "Over 60 days", outstanding, today, 61, int.MaxValue),
        ];

        var byCustomer = RankCustomers(outstanding, today);

        return Results.Ok(new ReceivablesReport(
            today,
            options.Value.Currency,
            Round(outstanding.Sum(i => i.Total)),
            Round(buckets.Where(b => b.Key != "current").Sum(b => b.Amount)),
            outstanding.Count,
            buckets,
            [.. byCustomer.Take(TopDebtorsLimit)],
            byCustomer.Count));
    }

    /// <summary>
    /// Groups by customer id rather than by name: two customers may share a display name, and
    /// merging them would overstate one debt and hide another.
    /// </summary>
    private static List<CustomerReceivable> RankCustomers(IEnumerable<Outstanding> outstanding, DateOnly today) =>
        [.. outstanding
            .GroupBy(i => i.CustomerId)
            .Select(customer => new CustomerReceivable(
                customer.First().CustomerName,
                customer.Count(),
                Round(customer.Sum(i => i.Total)),

                // "Overdue" draws the same line the buckets do: anything that is not `current`.
                Round(customer.Where(i => i.DueDate < today).Sum(i => i.Total))))
            .OrderByDescending(customer => customer.Outstanding)

            // Ties broken by name so the ranking is stable across calls. An assistant that names a
            // different top debtor each time it is asked is indistinguishable from a wrong one.
            .ThenBy(customer => customer.CustomerName, StringComparer.Ordinal)];

    private static AgingBucket BuildBucket(
        string key,
        string label,
        IEnumerable<Outstanding> outstanding,
        DateOnly today,
        int minDaysOverdue,
        int maxDaysOverdue)
    {
        var matching = outstanding
            .Where(i =>
            {
                var daysOverdue = today.DayNumber - i.DueDate.DayNumber;
                return daysOverdue >= minDaysOverdue && daysOverdue <= maxDaysOverdue;
            })
            .ToList();

        return new AgingBucket(key, label, matching.Count, Round(matching.Sum(i => i.Total)));
    }

    private static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
