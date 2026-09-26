using Microsoft.Win32;
using ScreenVault.Core.Recording;
using Serilog;

namespace ScreenVault.Core.SystemIntegration;

public sealed class SystemEventsMonitor : IDisposable
{
    private readonly RecordingController _controller;
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

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        Log.Information("Windows PowerModeChanged: {Mode}", e.Mode);
        if (e.Mode == PowerModes.Suspend)
        {
            // Suspend PC
            Task.Run(async () =>
            {
                try
                {
                    await _controller.PauseAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error pausing recording during system suspend.");
                }
            });
        }
        else if (e.Mode == PowerModes.Resume)
        {
            // Resume PC: wait 3 seconds for audio stack and monitors to re-enumerate
            Task.Run(async () =>
            {
                try
                {
                    Log.Information("Waiting 3 seconds after system resume for hardware stabilization...");
                    await Task.Delay(3000).ConfigureAwait(false);
                    await _controller.ResumeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error resuming recording after system resume.");
                }
            });
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        Log.Information("Windows SessionSwitch: {Reason}", e.Reason);
        _controller.AddMarker($"SessionSwitch: {e.Reason}", kind: "System");
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
