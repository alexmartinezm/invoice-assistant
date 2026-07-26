using Api.Domain;
using Api.Features.Invoices;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Features.Reports;

public sealed record AgingBucket(string Key, string Label, int InvoiceCount, decimal Amount);

public sealed record ReceivablesReport(
    DateOnly AsOf,
    string Currency,
    decimal TotalOutstanding,
    decimal TotalOverdue,
    int InvoiceCount,
    IReadOnlyList<AgingBucket> Buckets);

/// <summary>
/// The aging report exists so the model never has to add anything up. "How much are we owed?"
/// has to end in this endpoint; the calculation eval category guards that.
/// </summary>
public static class ReportEndpoints
{
    private sealed record Outstanding(DateOnly DueDate, decimal Total);

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
            .Select(i => new Outstanding(i.DueDate, i.Total))
            .ToListAsync(cancellationToken);

        List<AgingBucket> buckets =
        [
            BuildBucket("current", "Current", outstanding, today, int.MinValue, 0),
            BuildBucket("1-30", "1-30 days", outstanding, today, 1, 30),
            BuildBucket("31-60", "31-60 days", outstanding, today, 31, 60),
            BuildBucket("60+", "Over 60 days", outstanding, today, 61, int.MaxValue),
        ];

        return Results.Ok(new ReceivablesReport(
            today,
            options.Value.Currency,
            Round(outstanding.Sum(i => i.Total)),
            Round(buckets.Where(b => b.Key != "current").Sum(b => b.Amount)),
            outstanding.Count,
            buckets));
    }

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
