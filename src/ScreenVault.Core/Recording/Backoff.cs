namespace ScreenVault.Core.Recording;

public sealed class Backoff
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private int _consecutiveFailures;
    private DateTime _lastFailureUtc;
    private DateTime _healthySinceUtc = DateTime.UtcNow;

    public int ConsecutiveFailures => _consecutiveFailures;

    public void RecordFailure()
    {
        _consecutiveFailures++;
        _lastFailureUtc = DateTime.UtcNow;
    }

    public void RecordSuccess()
    {
        if ((DateTime.UtcNow - _healthySinceUtc).TotalSeconds >= 60.0)
        {
            _consecutiveFailures = 0;
        }
        _healthySinceUtc = DateTime.UtcNow;
    }

    public TimeSpan GetCurrentDelay()
    {
        if (_consecutiveFailures == 0) return TimeSpan.Zero;
        var index = Math.Min(_consecutiveFailures - 1, Schedule.Length - 1);
        return Schedule[index];
    }

    public bool IsBackoffElapsed()
    {
        if (_consecutiveFailures == 0) return true;
        return DateTime.UtcNow - _lastFailureUtc >= GetCurrentDelay();
    }

    public void Reset()
    {
        _consecutiveFailures = 0;
        _healthySinceUtc = DateTime.UtcNow;
    }
}
