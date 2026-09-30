using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Settings;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private readonly ToggleSwitch _chkStartWithWindows = new();
    private readonly ToggleSwitch _chkAutoStartRecording = new();
    private readonly ToggleSwitch _chkStartMinimized = new();
    private readonly NumberField _numStartupDelay = new() { Minimum = 0, Maximum = 120, Suffix = "s" };
    private readonly ToggleSwitch _chkConfirmStop = new();
    private readonly ModernComboBox _cmbTheme = new();

    private StackPanel BuildGeneralPage()
    {
        _cmbTheme.Items.AddRange(["Use Windows setting", "Light", "Dark"]);
        _cmbTheme.Width = 260;
        _numStartupDelay.Width = 110;

        var general = CreatePage("General", "Startup behavior, confirmations and appearance.");

        general.Controls.Add(Section("Startup"));
        general.Controls.Add(Card(
            Row("Start with Windows", "Open ScreenVault in the notification area when you sign in.", _chkStartWithWindows, Glyphs.Monitor),
            Row("Start recording automatically", "Begin recording as soon as ScreenVault starts, after the startup delay.", _chkAutoStartRecording, Glyphs.Record),
            Row("Start minimized to the tray", "Don't open the status window when ScreenVault starts.", _chkStartMinimized, Glyphs.Pin),
            Row("Startup delay", "Gives Windows audio time to get ready after you sign in.", _numStartupDelay, Glyphs.Clock)));

        general.Controls.Add(Section("Recording"));
        general.Controls.Add(Card(
            Row("Confirm before stopping", "Ask before a recording is stopped from the tray or the status window.", _chkConfirmStop, Glyphs.Stop)));

        general.Controls.Add(Section("Appearance"));
        general.Controls.Add(Card(
            Row("Theme", "Choose light or dark, or follow the app mode set in Windows.", _cmbTheme, Glyphs.Monitor)));

        return general;
    }

    private void LoadGeneralSettings(AppSettings s)
    {
        _chkStartWithWindows.Checked = s.General.StartWithWindows;
        _chkAutoStartRecording.Checked = s.General.StartRecordingOnLaunch;
        _chkStartMinimized.Checked = s.General.MinimizeToTrayOnLaunch;
        _numStartupDelay.Value = Math.Clamp(s.General.StartupDelaySeconds, 0, 120);
        _chkConfirmStop.Checked = s.General.ConfirmBeforeStop;
        _cmbTheme.SelectedIndex = s.General.Theme switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.Dark => 2,
            _ => 0
        };
    }

    private void SaveGeneralSettings(AppSettings s)
    {
        s.General.StartWithWindows = _chkStartWithWindows.Checked;
        s.General.StartRecordingOnLaunch = _chkAutoStartRecording.Checked;
        s.General.MinimizeToTrayOnLaunch = _chkStartMinimized.Checked;
        s.General.StartupDelaySeconds = (int)_numStartupDelay.Value;
        s.General.ConfirmBeforeStop = _chkConfirmStop.Checked;
        s.General.Theme = _cmbTheme.SelectedIndex switch
        {
            1 => AppThemeMode.Light,
            2 => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };
    }
}
