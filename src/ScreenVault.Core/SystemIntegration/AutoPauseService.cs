using System.Runtime.InteropServices;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.SystemIntegration;

/// <summary>
/// Monitors user activity and system state, automatically pausing and resuming the recording
/// according to the AutoPauseSettings.
/// </summary>
public sealed class AutoPauseService : IDisposable
{
    // ── P/Invoke ────────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // ── Events ──────────────────────────────────────────────────────────────────
    public event EventHandler<PauseReason>? AutoPaused;
    public event EventHandler? AutoResumed;

    // ── Dependencies ─────────────────────────────────────────────────────────────
    private readonly IPausableRecorder _recorder;
    private readonly ISettingsService _settings;
    private readonly MeetingDetector _meetingDetector;

    // ── State ────────────────────────────────────────────────────────────────────
    private readonly System.Threading.Timer _timer;
    private PauseReason? _currentAutoPauseReason;
    private bool _isDisposed;
    private bool _sessionLocked;
    private bool _suspendPending;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    public AutoPauseService(
        IPausableRecorder recorder,
        ISettingsService settings,
        MeetingDetector meetingDetector)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _meetingDetector = meetingDetector ?? throw new ArgumentNullException(nameof(meetingDetector));
        _timer = new System.Threading.Timer(OnTick, null, CheckInterval, CheckInterval);
        Log.Information("AutoPauseService started.");
    }

    // ── Called by SystemEventsMonitor ────────────────────────────────────────────
    public void OnSessionLocked()
    {
        _sessionLocked = true;
        if (!_settings.Current.AutoPause.PauseWhenLocked) return;
        _ = AutoPauseAsync(PauseReason.Locked);
    }

    public void OnSessionUnlocked()
    {
        _sessionLocked = false;
        _lastActivityUtc = DateTime.UtcNow;
        _ = AutoResumeAsync();
    }

    public void OnSuspend()
    {
        _suspendPending = true;
        if (!_settings.Current.AutoPause.PauseOnSleep) return;
        _ = AutoPauseAsync(PauseReason.Asleep);
    }

    public void OnResume()
    {
        _suspendPending = false;
        _lastActivityUtc = DateTime.UtcNow;
        // If the lock screen is showing after wake, stay in Locked state
        if (_sessionLocked && _settings.Current.AutoPause.PauseWhenLocked)
        {
            _ = AutoPauseAsync(PauseReason.Locked);
            return;
        }
        _ = AutoResumeAsync();
    }

    public void OnDisplayOff()
    {
        if (!_settings.Current.AutoPause.PauseOnSleep) return;
        Log.Information("AutoPauseService: Display off detected, pausing.");
        _recorder.AddMarker("Paused: display off", kind: "AutoPause");
        _ = AutoPauseAsync(PauseReason.Away);
    }

    public void OnDisplayOn()
    {
        if (!_settings.Current.AutoPause.ResumeWhenBack) return;
        if (_sessionLocked) return;
        _lastActivityUtc = DateTime.UtcNow;
        if (_currentAutoPauseReason == PauseReason.Away)
        {
            Log.Information("AutoPauseService: Display on detected, resuming.");
            _recorder.AddMarker("Resumed: display on", kind: "AutoPause");
            _ = AutoResumeAsync();
        }
    }

    // ── Periodic check ────────────────────────────────────────────────────────────
    private void OnTick(object? _)
    {
        if (_isDisposed || _suspendPending) return;
        try
        {
            var cfg = _settings.Current.AutoPause;
            if (!cfg.PauseWhenAway) return;

            var idleTime = GetIdleTime();
            var threshold = TimeSpan.FromMinutes(cfg.AwayMinutes);

            // Check if something counts as activity (calls or sound)
            bool exceptionActive = false;
            if (cfg.KeepRecordingDuringCalls && _meetingDetector.IsMeetingActive)
                exceptionActive = true;

            if (exceptionActive)
            {
                _lastActivityUtc = DateTime.UtcNow;
                // Resume if we were auto-paused for Away
                if (_currentAutoPauseReason == PauseReason.Away)
                    _ = AutoResumeAsync();
                return;
            }

            bool isAway = idleTime >= threshold;

            if (isAway && _currentAutoPauseReason == null && _recorder.IsRecording)
            {
                int minutes = (int)idleTime.TotalMinutes;
                Log.Information("AutoPauseService: No activity for {Minutes} min, pausing.", minutes);
                _recorder.AddMarker($"Paused: no activity for {minutes} min", kind: "AutoPause");
                _ = AutoPauseAsync(PauseReason.Away);
            }
            else if (!isAway && _currentAutoPauseReason == PauseReason.Away && cfg.ResumeWhenBack)
            {
                Log.Information("AutoPauseService: Activity detected, resuming.");
                _recorder.AddMarker("Resumed: you're back", kind: "AutoPause");
                _ = AutoResumeAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AutoPauseService tick error.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────
    private async Task AutoPauseAsync(PauseReason reason)
    {
        // Don't override a stronger reason
        if (_currentAutoPauseReason == PauseReason.Locked && reason == PauseReason.Away) return;
        if (_currentAutoPauseReason == PauseReason.Asleep) return;

        // Don't override a manual pause
        if (_recorder.IsManuallyPaused) return;

        _currentAutoPauseReason = reason;
        try
        {
            await _recorder.PauseAsync(reason).ConfigureAwait(false);
            AutoPaused?.Invoke(this, reason);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AutoPauseService: Error during auto-pause.");
        }
    }

    private async Task AutoResumeAsync()
    {
        if (_currentAutoPauseReason == null) return;
        if (!_settings.Current.AutoPause.ResumeWhenBack) return;

        _currentAutoPauseReason = null;
        try
        {
            await _recorder.ResumeAsync().ConfigureAwait(false);
            AutoResumed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AutoPauseService: Error during auto-resume.");
        }
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // Handle 32-bit tick wrap-around
        uint now = (uint)Environment.TickCount;
        uint idleTicks = now - info.dwTime;
        return TimeSpan.FromMilliseconds(idleTicks);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _timer.Dispose();
        Log.Information("AutoPauseService disposed.");
    }
}
