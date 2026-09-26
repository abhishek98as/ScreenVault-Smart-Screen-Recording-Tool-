using ScreenVault.Core.Output;
using Serilog;

namespace ScreenVault.Core.SystemIntegration;

public sealed class MidnightTimer : IDisposable
{
    private readonly ISegmentSink _segmentSink;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();
    private bool _isDisposed;

    public MidnightTimer(ISegmentSink segmentSink)
    {
        _segmentSink = segmentSink ?? throw new ArgumentNullException(nameof(segmentSink));
        _timer = new System.Threading.Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        ScheduleNextMidnight();
    }

    private void ScheduleNextMidnight()
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            var now = DateTime.Now;
            var nextMidnight = now.Date.AddDays(1);
            var delay = nextMidnight - now;
            if (delay <= TimeSpan.Zero)
            {
                delay = TimeSpan.FromMinutes(1);
            }

            Log.Information("MidnightTimer scheduled for next midnight in {Hours:F1} hours ({Time})",
                delay.TotalHours, nextMidnight);

            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerElapsed(object? state)
    {
        try
        {
            Log.Information("Midnight timer elapsed. Requesting midnight segment rotation.");
            _segmentSink.RequestRotation(RotationReason.Midnight);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error requesting midnight rotation.");
        }
        finally
        {
            ScheduleNextMidnight();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _timer.Dispose();
        }
    }
}
