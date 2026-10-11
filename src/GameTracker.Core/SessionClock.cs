namespace GameTracker.Core;

// Feed a monotonic millisecond timestamp. Long gaps (sleep/stall) are not counted.
public sealed class SessionClock
{
    private long? previous;
    public long DurationMs { get; private set; }
    public void Observe(long now, bool present)
    {
        if (present && previous is { } before)
        {
            var delta = now - before;
            if (delta is >= 0 and <= 6000) DurationMs += delta;
        }
        previous = present ? now : null;
    }
    public void Suspend() => previous = null;
}
