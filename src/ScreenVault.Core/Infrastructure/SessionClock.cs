namespace ScreenVault.Core.Infrastructure;

public sealed class SessionClock
{
    private readonly IClock _clock;
    private TimeSpan _completed;          // sum of finished recording spans in this session
    private long? _runningSince;          // Stopwatch timestamp when current span started
    private long? _partRunningSince;      // Timestamp when current part span started

    public SessionClock(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void StartSpan()
    {
        _runningSince ??= _clock.Timestamp;
        _partRunningSince ??= _clock.Timestamp;
    }

    public void StartPartSpan()
    {
        _partRunningSince = _clock.Timestamp;
        _runningSince ??= _clock.Timestamp;
    }

    public void StopSpan()
    {
        if (_runningSince is long start)
        {
            _completed += TimeSpan.FromSeconds(_clock.SecondsBetween(start, _clock.Timestamp));
            _runningSince = null;
        }
        _partRunningSince = null;
    }

    public void Reset()
    {
        _completed = TimeSpan.Zero;
        _runningSince = null;
        _partRunningSince = null;
    }

    public TimeSpan Elapsed => _runningSince is long s
        ? _completed + TimeSpan.FromSeconds(_clock.SecondsBetween(s, _clock.Timestamp))
        : _completed;

    public TimeSpan CurrentPartElapsed => _partRunningSince is long ps
        ? TimeSpan.FromSeconds(_clock.SecondsBetween(ps, _clock.Timestamp))
        : TimeSpan.Zero;

    public bool IsRunning => _runningSince.HasValue;
}
