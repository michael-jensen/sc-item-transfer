namespace ItemCopy.Tests.Fakes;

/// <summary>A clock that only moves when a test (or a fake delay) advances it.</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly Lock _lock = new();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
            return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
            _now += by;
    }

    /// <summary>A delay function that advances this clock instead of waiting.</summary>
    public Task Delay(TimeSpan by, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Advance(by);
        return Task.CompletedTask;
    }
}
