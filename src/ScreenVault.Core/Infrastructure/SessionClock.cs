namespace ScreenVault.Core.Infrastructure;

/// <summary>
/// Recorded time of a session (paused time excluded). Read by the UI and the marker service
/// while the recording pipeline updates it, so all access is synchronized.
/// </summary>
public sealed class SessionClock
{
    private readonly IClock _clock;
    private readonly object _lock = new();
    private TimeSpan _completed;          // sum of finished recording spans in this session
    private long? _runningSince;          // Stopwatch timestamp when current span started
    private long? _partRunningSince;      // Timestamp when current part span started

    public SessionClock(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void StartSpan()
    {
        lock (_lock)
        {
            _runningSince ??= _clock.Timestamp;
            _partRunningSince ??= _clock.Timestamp;
        }
    }

    public void StartPartSpan()
    {
        lock (_lock)
        {
            _partRunningSince = _clock.Timestamp;
            _runningSince ??= _clock.Timestamp;
        }
    }

    public void StopSpan()
    {
        lock (_lock)
        {
            if (_runningSince is long start)
            {
                _completed += TimeSpan.FromSeconds(_clock.SecondsBetween(start, _clock.Timestamp));
                _runningSince = null;
            }

            _partRunningSince = null;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _completed = TimeSpan.Zero;
            _runningSince = null;
            _partRunningSince = null;
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                return _runningSince is long s
                    ? _completed + TimeSpan.FromSeconds(_clock.SecondsBetween(s, _clock.Timestamp))
                    : _completed;
            }
        }
    }

    public TimeSpan CurrentPartElapsed
    {
        get
        {
            lock (_lock)
            {
                return _partRunningSince is long ps
                    ? TimeSpan.FromSeconds(_clock.SecondsBetween(ps, _clock.Timestamp))
                    : TimeSpan.Zero;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _runningSince.HasValue;
            }
        }
    }
}
