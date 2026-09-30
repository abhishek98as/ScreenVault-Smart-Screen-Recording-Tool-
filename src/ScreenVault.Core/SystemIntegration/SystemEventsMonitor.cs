using Microsoft.Win32;
using ScreenVault.Core.Recording;
using Serilog;

namespace ScreenVault.Core.SystemIntegration;

public sealed class SystemEventsMonitor : IDisposable
{
    private readonly RecordingController _controller;
    private AutoPauseService? _autoPauseService;
    private bool _isDisposed;

    public SystemEventsMonitor(RecordingController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
            Log.Information("SystemEventsMonitor initialized and registered for Windows events.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register some SystemEvents listeners.");
        }
    }

    /// <summary>Wire up the AutoPauseService so it receives power and session events.</summary>
    public void SetAutoPauseService(AutoPauseService service)
    {
        _autoPauseService = service;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        Log.Information("Windows PowerModeChanged: {Mode}", e.Mode);
        if (e.Mode == PowerModes.Suspend)
        {
            _autoPauseService?.OnSuspend();

            // Legacy path: keep existing suspend-pause when AutoPause is disabled
            if (_autoPauseService == null && _controller.Desired == DesiredState.Recording)
            {
                Task.Run(async () =>
                {
                    try { await _controller.PauseAsync().ConfigureAwait(false); }
                    catch (Exception ex) { Log.Error(ex, "Error pausing on system suspend."); }
                });
            }
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _autoPauseService?.OnResume();

            if (_autoPauseService == null)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000).ConfigureAwait(false);
                        await _controller.ResumeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) { Log.Error(ex, "Error resuming after system resume."); }
                });
            }
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        Log.Information("Windows SessionSwitch: {Reason}", e.Reason);

        // Notify AutoPauseService for lock/unlock
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _autoPauseService?.OnSessionLocked();
                break;
            case SessionSwitchReason.SessionUnlock:
                _autoPauseService?.OnSessionUnlocked();
                break;
        }

        // Always add the session marker
        if (_controller.State is RecorderState.Recording or RecorderState.Paused)
        {
            var label = e.Reason switch
            {
                SessionSwitchReason.SessionLock => "Screen locked",
                SessionSwitchReason.SessionUnlock => "Screen unlocked",
                SessionSwitchReason.RemoteConnect => "Remote desktop connected",
                SessionSwitchReason.RemoteDisconnect => "Remote desktop disconnected",
                SessionSwitchReason.ConsoleConnect => "Switched to this session",
                SessionSwitchReason.ConsoleDisconnect => "Switched away from this session",
                _ => $"Session change: {e.Reason}"
            };
            _controller.AddMarker(label, kind: "System");
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Log.Information("Windows DisplaySettingsChanged event detected.");
        _controller.TriggerDisplayChanged();
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        Log.Information("Windows SessionEnding event detected: {Reason}", e.Reason);
        try
        {
            _controller.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping recording on session ending.");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
        }
        catch
        {
            // Ignore during shutdown
        }
    }
}
