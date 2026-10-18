using TrayCer.Core.Models;

namespace TrayCer.Core.Monitoring;

public sealed record MonitorOptions
{
    /// <summary>Choices offered in settings for the interval while printing.</summary>
    public static readonly IReadOnlyList<TimeSpan> ActiveIntervalChoices =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    public static readonly TimeSpan DefaultActiveInterval = TimeSpan.FromSeconds(15);

    /// <summary>While a print runs, or while the flyout is open.</summary>
    public TimeSpan ActiveInterval { get; init; } = DefaultActiveInterval;

    /// <summary>Idle HTTP printers: one cheap request a minute notices a new print soon enough.</summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Idle push printers reconnect to check, which costs more (and P1/A1 dislike pushall).</summary>
    public TimeSpan PersistentIdleInterval { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxRetryInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a failing printer keeps showing its last reading before it reads as offline.</summary>
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Snaps a stored interval to the nearest offered choice, so a hand-edited setting can't hammer a printer.</summary>
    public static TimeSpan SanitizeActiveInterval(double seconds) =>
        ActiveIntervalChoices.OrderBy(c => Math.Abs(c.TotalSeconds - seconds)).First();
}

/// <summary>When to look at a printer next.</summary>
public static class PollPolicy
{
    public static TimeSpan NextDelay(PrinterSnapshot? snapshot, int consecutiveFailures, bool foreground, bool persistent, MonitorOptions options)
    {
        if (consecutiveFailures > 0)
        {
            // Back off while it's unreachable (likely switched off), but not past the active
            // interval while the user is looking at it.
            double factor = Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
            TimeSpan backoff = TimeSpan.FromTicks((long)Math.Min(options.RetryInterval.Ticks * factor, options.MaxRetryInterval.Ticks));
            return foreground ? Min(backoff, options.ActiveInterval * 2) : backoff;
        }

        if (foreground || snapshot is { IsActive: true })
        {
            return options.ActiveInterval;
        }

        return persistent ? options.PersistentIdleInterval : options.IdleInterval;
    }

    /// <summary>Whether a push driver should close its connection until the next check.</summary>
    public static bool ShouldSuspend(PrinterSnapshot? snapshot, bool foreground, bool persistent) =>
        persistent && !foreground && snapshot is not { IsActive: true };

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
