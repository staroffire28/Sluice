namespace Sluice.Portal.Tests;

internal sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset? start = null) =>
        _utcNow = start ?? new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan span) => _utcNow = _utcNow.Add(span);

    public override DateTimeOffset GetUtcNow() => _utcNow;
}