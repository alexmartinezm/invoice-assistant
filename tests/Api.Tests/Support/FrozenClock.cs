using Api.Infrastructure;

namespace Api.Tests.Support;

/// <summary>
/// Freezes "now" for the length of a test run, so the seed, the overdue flag and the aging
/// buckets cannot shift midway through. It freezes at the real current instant rather than at a
/// hard-coded date: tokens are validated against the system clock, and a date pinned in the past
/// would make every JWT look expired.
/// </summary>
public sealed class FrozenClock : IClock
{
    public FrozenClock()
        : this(DateTimeOffset.UtcNow)
    {
    }

    public FrozenClock(DateTimeOffset now)
    {
        UtcNow = now;
        Today = DateOnly.FromDateTime(now.UtcDateTime);
    }

    public DateTimeOffset UtcNow { get; }

    public DateOnly Today { get; }
}
