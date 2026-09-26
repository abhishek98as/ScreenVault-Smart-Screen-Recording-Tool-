using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.SystemIntegration;

public interface IReminderService : IDisposable
{
    void Start();
    void StopReminders();
    void Snooze(TimeSpan duration);
    void SnoozeForToday();
    event EventHandler? ReminderTriggered;
}

[SupportedOSPlatform("windows")]
public sealed class ReminderService : IReminderService
{
    private readonly ISettingsService _settingsService;
    private readonly IRecordingController _controller;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();

    private DateTime? _readySinceUtc;
    private DateTime? _snoozedUntilUtc;
    private DateTime? _snoozedDay;
    private DateTime? _lastPromptedUtc;
    private bool _isRunning;
    private bool _isDisposed;

    public event EventHandler? ReminderTriggered;

    public ReminderService(ISettingsService settingsService, IRecordingController controller)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        _controller.HealthChanged += OnHealthChanged;
        _timer = new System.Threading.Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_isDisposed || _isRunning) return;
            _isRunning = true;
            if (_controller.State == RecorderState.Idle)
            {
                _readySinceUtc = DateTime.UtcNow;
            }
            _timer.Change(10000, 30000); // Check every 30s
            Log.Information("ReminderService started.");
        }
    }

    public void StopReminders()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Log.Information("ReminderService stopped.");
        }
    }

    public void Snooze(TimeSpan duration)
    {
        lock (_lock)
        {
            _snoozedUntilUtc = DateTime.UtcNow + duration;
            Log.Information("Recording reminder snoozed until {Time} UTC", _snoozedUntilUtc);
        }
    }

    public void SnoozeForToday()
    {
        lock (_lock)
        {
            _snoozedDay = DateTime.Today;
            Log.Information("Recording reminder snoozed for the rest of today.");
        }
    }

    private void OnHealthChanged(object? sender, HealthSnapshot health)
    {
        lock (_lock)
        {
            if (health.State == RecorderState.Idle)
            {
                _readySinceUtc ??= DateTime.UtcNow;
            }
            else
            {
                _readySinceUtc = null;
            }
        }
    }

    private void OnTimerTick(object? state)
    {
        if (!_isRunning || _isDisposed) return;

        var config = _settingsService.Current.Reminders;
        if (!config.Enabled) return;

        var nowUtc = DateTime.UtcNow;
        var nowLocal = DateTime.Now;

        lock (_lock)
        {
            // 1. Check snoozed for today
            if (_snoozedDay.HasValue && _snoozedDay.Value.Date == DateTime.Today) return;

            // 2. Check snoozed duration
            if (_snoozedUntilUtc.HasValue && nowUtc < _snoozedUntilUtc.Value) return;

            // 3. Must be in Ready/Idle state
            if (_controller.State != RecorderState.Idle || !_readySinceUtc.HasValue) return;

            // 4. Must be within configured work days
            var dayCode = nowLocal.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture);
            if (!config.WorkDays.Any(d => string.Equals(d, dayCode, StringComparison.OrdinalIgnoreCase))) return;

            // 5. Must be within configured work hours
            if (TimeSpan.TryParse(config.WorkStart, out var start) && TimeSpan.TryParse(config.WorkEnd, out var end))
            {
                var timeOfDay = nowLocal.TimeOfDay;
                if (timeOfDay < start || timeOfDay > end) return;
            }

            // 6. Check user idle time via GetLastInputInfo (must be active, idle < 5 min)
            var idle = GetIdleTime();
            if (idle > TimeSpan.FromMinutes(5)) return;

            // 7. Check ready duration >= RemindAfterMinutes
            var readyDuration = nowUtc - _readySinceUtc.Value;
            if (readyDuration < TimeSpan.FromMinutes(config.RemindAfterMinutes)) return;

            // 8. Repeat interval
            if (_lastPromptedUtc.HasValue)
            {
                var sincePrompt = nowUtc - _lastPromptedUtc.Value;
                if (sincePrompt < TimeSpan.FromMinutes(config.RepeatEveryMinutes)) return;
            }

            _lastPromptedUtc = nowUtc;
        }

        Log.Information("Triggering 'Not recording' reminder.");
        ReminderTriggered?.Invoke(this, EventArgs.Empty);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private static TimeSpan GetIdleTime()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (GetLastInputInfo(ref lii))
        {
            var idleTicks = (uint)Environment.TickCount - lii.dwTime;
            return TimeSpan.FromMilliseconds(idleTicks);
        }
        return TimeSpan.Zero;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _isRunning = false;
            _timer.Dispose();
        }
    }
}
