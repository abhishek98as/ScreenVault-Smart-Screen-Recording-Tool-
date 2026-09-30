using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Settings;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private readonly ToggleSwitch _chkNotifyDevice = new();
    private readonly ToggleSwitch _chkNotifyStorage = new();
    private readonly ToggleSwitch _chkNotifyAutoPause = new();
    private readonly ToggleSwitch _chkNotifyRecordingSaved = new();
    private readonly ToggleSwitch _chkNotifyMarkers = new();
    private readonly ToggleSwitch _chkNotifyPerformance = new();

    private StackPanel BuildNotificationsPage()
    {
        var page = CreatePage("Notifications", "Configure which desktop notification toasts ScreenVault displays.");

        page.Controls.Add(Section("Events"));
        page.Controls.Add(Card(
            Row("Audio device changes", "Notify when the microphone or speakers switch. Recording always continues.", _chkNotifyDevice, Glyphs.Headphones),
            Row("Storage events", "Notify when recording moves to another drive or space runs low.", _chkNotifyStorage, Glyphs.HardDrive),
            Row("Auto-pause and resume", "Notify when recording pauses due to inactivity, lock, sleep, or resumes.", _chkNotifyAutoPause, Glyphs.Pause),
            Row("Recording saved", "Notify when a recording finishes and has been saved to disk.", _chkNotifyRecordingSaved, Glyphs.Completed),
            Row("Markers added", "Notify when a bookmark marker is placed during recording.", _chkNotifyMarkers, Glyphs.Flag)));

        page.Controls.Add(Section("Health & performance"));
        page.Controls.Add(Card(
            Row("Performance warnings", "Notify if the encoder struggles or frame rate drops (critical errors are always shown).", _chkNotifyPerformance, Glyphs.Warning)));

        return page;
    }

    private void LoadNotificationsSettings(AppSettings s)
    {
        _chkNotifyDevice.Checked = s.General.Notifications.DeviceSwitch;
        _chkNotifyStorage.Checked = s.General.Notifications.Storage;
        _chkNotifyAutoPause.Checked = s.General.Notifications.AutoPauseResume;
        _chkNotifyRecordingSaved.Checked = s.General.Notifications.RecordingSaved;
        _chkNotifyMarkers.Checked = s.General.Notifications.Markers;
        _chkNotifyPerformance.Checked = s.General.Notifications.PerformanceWarnings;
    }

    private void SaveNotificationsSettings(AppSettings s)
    {
        s.General.Notifications.DeviceSwitch = _chkNotifyDevice.Checked;
        s.General.Notifications.Storage = _chkNotifyStorage.Checked;
        s.General.Notifications.AutoPauseResume = _chkNotifyAutoPause.Checked;
        s.General.Notifications.RecordingSaved = _chkNotifyRecordingSaved.Checked;
        s.General.Notifications.Markers = _chkNotifyMarkers.Checked;
        s.General.Notifications.PerformanceWarnings = _chkNotifyPerformance.Checked;
    }
}
