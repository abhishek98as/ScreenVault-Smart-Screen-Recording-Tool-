using System.Globalization;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

public interface INotificationPresenter
{
    void ShowMicSwitched(string deviceName);
    void ShowOutputSwitched(string deviceName);
    void ShowDeviceSwitched(string summary);
    void ShowStorageFailover(string primaryDrive, string backupPath);
    void ShowDriveRemoved(string failoverPath);
    void ShowEncoderRestarted();
    void ShowRecovered(string timeRange);
    void ShowAllStorageLow();
    void ShowFaulted(string reason);
    void ShowSecondLaunch(TimeSpan elapsed);
    void ShowPausedReminder();
    void ShowMarkerAdded(DateTime timeLocal);
    void ShowWelcome();
    void ShowFpsDegraded(int fps);
    void ShowMeetingStarted(string appName);
    void ShowNoAudioSources();
    void ShowSaved(string sessionId, string filePath);
    void ShowInfo(string title, string message);
}

public sealed class NotificationPresenter : INotificationPresenter
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ISettingsService _settingsService;
    private readonly Dictionary<string, DateTime> _lastShown = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private volatile string? _lastShownKey;

    /// <summary>Key of the most recent notification, so a click on it can do the right thing.</summary>
    public string? LastShownKey => _lastShownKey;

    public const string PausedReminderKey = "PausedReminder";

    public NotificationPresenter(NotifyIcon notifyIcon, ISettingsService settingsService)
    {
        _notifyIcon = notifyIcon ?? throw new ArgumentNullException(nameof(notifyIcon));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public void ShowMicSwitched(string deviceName)
    {
        if (!_settingsService.Current.General.Notifications.DeviceSwitch) return;
        ShowRateLimited("MicSwitched", TimeSpan.FromSeconds(30),
            "Microphone Changed",
            $"Microphone switched to '{deviceName}'. Recording continues.",
            ToolTipIcon.Info);
    }

    public void ShowOutputSwitched(string deviceName)
    {
        if (!_settingsService.Current.General.Notifications.DeviceSwitch) return;
        ShowRateLimited("OutputSwitched", TimeSpan.FromSeconds(30),
            "Audio Output Changed",
            $"System audio now captured from '{deviceName}'. Recording continues.",
            ToolTipIcon.Info);
    }

    public void ShowDeviceSwitched(string summary)
    {
        if (!_settingsService.Current.General.Notifications.DeviceSwitch) return;
        ShowRateLimited("DeviceSwitched", TimeSpan.FromSeconds(10),
            "Audio Device Changed",
            $"{summary}. Recording continues.",
            ToolTipIcon.Info);
    }

    public void ShowStorageFailover(string primaryDrive, string backupPath)
    {
        if (!_settingsService.Current.General.Notifications.Storage) return;
        ShowRateLimited("StorageFailover", TimeSpan.FromSeconds(60),
            "Storage Failover",
            $"Primary drive ({primaryDrive}) is low on space. Now saving to {backupPath}.",
            ToolTipIcon.Warning);
    }

    public void ShowDriveRemoved(string failoverPath)
    {
        if (!_settingsService.Current.General.Notifications.Storage) return;
        ShowRateLimited("DriveRemoved", TimeSpan.FromSeconds(60),
            "Storage Location Unavailable",
            $"Storage drive disconnected. Now saving to {failoverPath} — nothing was lost.",
            ToolTipIcon.Warning);
    }

    public void ShowEncoderRestarted()
    {
        ShowRateLimited("EncoderRestarted", TimeSpan.FromMinutes(2),
            "Recording Restarted",
            "Recording restarted after an encoder error (about 1 s gap).",
            ToolTipIcon.Warning);
    }

    public void ShowRecovered(string timeRange)
    {
        ShowRateLimited("Recovered", TimeSpan.FromSeconds(10),
            "ScreenVault Recovered",
            $"Recovered interrupted recording ({timeRange}).",
            ToolTipIcon.Info);
    }

    public void ShowAllStorageLow()
    {
        ShowRateLimited("AllStorageLow", TimeSpan.FromMinutes(15),
            "Storage Space Critical",
            "All storage locations are almost full! Free up space to keep recording.",
            ToolTipIcon.Error);
    }

    public void ShowFaulted(string reason)
    {
        ShowRateLimited("Faulted", TimeSpan.FromMinutes(1),
            "ScreenVault Error",
            $"ScreenVault can't record right now: {reason}. Retrying…",
            ToolTipIcon.Error);
    }

    public void ShowSecondLaunch(TimeSpan elapsed)
    {
        var formatted = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        ShowRateLimited("SecondLaunch", TimeSpan.FromSeconds(5),
            "ScreenVault Running",
            $"ScreenVault is already running — recording for {formatted}.",
            ToolTipIcon.Info);
    }

    public void ShowPausedReminder()
    {
        ShowRateLimited(PausedReminderKey, TimeSpan.FromMinutes(10),
            "Recording Paused",
            "Recording has been paused for 10 minutes. Click here to resume.",
            ToolTipIcon.Warning);
    }

    public void ShowMarkerAdded(DateTime timeLocal)
    {
        ShowRateLimited("MarkerAdded", TimeSpan.FromSeconds(5),
            "Marker Added",
            $"Marker added at {timeLocal:HH:mm:ss}.",
            ToolTipIcon.Info);
    }

    public void ShowWelcome()
    {
        ShowRateLimited("Welcome", TimeSpan.FromMinutes(1),
            "ScreenVault is running",
            "Find it in the notification area near the clock. Click its icon to start recording or check the status.",
            ToolTipIcon.Info);
    }

    public void ShowFpsDegraded(int fps)
    {
        ShowRateLimited("FpsDegraded", TimeSpan.FromMinutes(2),
            "Encoding Performance Warning",
            $"Encoder couldn't keep up. Lowered frame rate to {fps} fps to prevent dropping frames.",
            ToolTipIcon.Warning);
    }

    public void ShowMeetingStarted(string appName)
    {
        ShowRateLimited("MeetingStarted", TimeSpan.FromSeconds(30),
            "Meeting Detected",
            $"Recording started — {appName} meeting detected.",
            ToolTipIcon.Info);
    }

    public void ShowNoAudioSources()
    {
        ShowRateLimited("NoAudioSources", TimeSpan.FromSeconds(60),
            "Audio Source Missing",
            "Recording has no audio source — check your devices",
            ToolTipIcon.Warning);
    }

    public void ShowSaved(string sessionId, string filePath)
    {
        var fileName = !string.IsNullOrEmpty(filePath) ? System.IO.Path.GetFileName(filePath) : sessionId;
        ShowRateLimited("Saved_" + sessionId, TimeSpan.FromSeconds(5),
            "Recording Saved",
            $"ScreenVault saved recording: {fileName}",
            ToolTipIcon.Info);
    }

    public void ShowInfo(string title, string message)
    {
        ShowRateLimited("Info_" + title, TimeSpan.FromSeconds(5),
            title,
            message,
            ToolTipIcon.Info);
    }

    private void ShowRateLimited(string key, TimeSpan minInterval, string title, string message, ToolTipIcon icon)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_lastShown.TryGetValue(key, out var lastTime) && (now - lastTime) < minInterval)
            {
                Log.Debug("Notification for {Key} suppressed by rate limit", key);
                return;
            }

            _lastShown[key] = now;
            _lastShownKey = key;
        }

        try
        {
            _notifyIcon.ShowBalloonTip(4000, title, message, icon);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to display balloon tip for {Key}", key);
        }
    }
}
