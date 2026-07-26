namespace Api.Infrastructure;

/// <summary>
/// "Overdue" and the aging report are both defined against the current date, so the current
/// date is a dependency rather than a static call: tests pin it and get deterministic buckets.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateOnly Today { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}
