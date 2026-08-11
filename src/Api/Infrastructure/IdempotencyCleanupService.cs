using Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure;

/// <summary>
/// Deletes expired idempotency receipts, in bounded batches.
/// </summary>
/// <remarks>
/// It exists because the old design expired rows by <em>ignoring</em> them on read while the unique
/// index kept reserving them for ever. A key could therefore be simultaneously too old to replay and
/// already taken: the lookup would miss it, the insert would collide, and the caller would get a
/// duplicate-key error for a request that looked new. Expiry is now a deletion, so the two rules
/// cannot disagree.
/// </remarks>
public sealed class IdempotencyCleanupService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<IdempotencyCleanupService> logger)
    : BackgroundService
{
    /// <summary>Rows per pass. A cap keeps the delete off the hot path of whatever else is running.</summary>
    public const int BatchSize = 500;

    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // The first pass waits: startup already has migrations and seeding to get through, and a
        // month-old receipt is not urgent.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var removed = await PurgeAsync(stoppingToken);

                if (removed > 0)
                {
                    logger.LogInformation("Removed {Count} expired idempotency receipts.", removed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed cleanup is not a reason to stop cleaning up; the next tick tries again.
                logger.LogWarning(exception, "Idempotency cleanup failed.");
            }
        }
    }

    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.UtcNow;

        var expired = await db.IdempotencyRecords
            .Where(record => record.ExpiresAt != null && record.ExpiresAt <= now)
            .OrderBy(record => record.ExpiresAt)
            .Select(record => record.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        return await db.IdempotencyRecords
            .Where(record => expired.Contains(record.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
