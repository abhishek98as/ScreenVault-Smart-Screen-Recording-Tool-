using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Settings;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private readonly ToggleSwitch _chkPauseWhenAway = new();
    private readonly NumberField _numAwayMinutes = new() { Minimum = 1, Maximum = 240, Value = 10, Suffix = "min" };
    private readonly ToggleSwitch _chkKeepRecordingCalls = new();
    private readonly ToggleSwitch _chkKeepRecordingSound = new();
    private readonly ToggleSwitch _chkResumeWhenBack = new();
    private readonly ToggleSwitch _chkPauseWhenLocked = new();
    private readonly ToggleSwitch _chkPauseOnSleep = new();
    private readonly ModernComboBox _cmbPausedReminder = new();
    private readonly List<int> _pausedReminderValues = [0, 5, 10, 15, 30, 60];

    private StackPanel BuildPauseResumePage()
    {
        _cmbPausedReminder.Items.AddRange(["Off", "5 minutes", "10 minutes (recommended)", "15 minutes", "30 minutes", "60 minutes"]);
        _cmbPausedReminder.Width = 260;
        _numAwayMinutes.Width = 110;

        _chkPauseWhenAway.CheckedChanged += (_, _) =>
        {
            _numAwayMinutes.Enabled = _chkPauseWhenAway.Checked;
            _chkKeepRecordingCalls.Enabled = _chkPauseWhenAway.Checked;
            _chkKeepRecordingSound.Enabled = _chkPauseWhenAway.Checked;
            _chkResumeWhenBack.Enabled = _chkPauseWhenAway.Checked;
        };

        var page = CreatePage("Pause & resume", "Automatically pause and resume recording based on user activity, lock state, and power modes.");

        page.Controls.Add(Section("When you're away"));
        page.Controls.Add(Card(
            Row("Pause when I'm away", "Automatically pause recording when no mouse or keyboard input is detected.", _chkPauseWhenAway, Glyphs.Pause),
            Row("Away timeout", "Time with no input before recording is paused.", _numAwayMinutes, Glyphs.Clock),
            Row("Keep recording during calls", "Do not pause while a conference call (Teams, Zoom, browser) is using the microphone.", _chkKeepRecordingCalls, Glyphs.Headphones),
            Row("Keep recording while sound plays", "Do not pause while computer audio or video is playing through speakers or headset.", _chkKeepRecordingSound, Glyphs.Volume),
            Row("Resume when I'm back", "Automatically resume recording as soon as mouse or keyboard input is detected again.", _chkResumeWhenBack, Glyphs.Play)));

        page.Controls.Add(Section("System events"));
        page.Controls.Add(Card(
            Row("Pause when locked", "Pause recording when the workstation is locked (Win+L) or disconnected, and resume on unlock.", _chkPauseWhenLocked, Glyphs.Lock),
            Row("Pause on sleep or display off", "Pause immediately when the PC goes to sleep or the screen turns off.", _chkPauseOnSleep, Glyphs.Monitor)));

        page.Controls.Add(Section("Reminders"));
        page.Controls.Add(Card(
            Row("Remind me while paused", "Shows a reminder notification when recording stays paused.", _cmbPausedReminder, Glyphs.Flag)));

        return page;
    }

    private void LoadPauseResumeSettings(AppSettings s)
    {
        _chkPauseWhenAway.Checked = s.AutoPause.PauseWhenAway;
        _numAwayMinutes.Value = Math.Clamp(s.AutoPause.AwayMinutes, 1, 240);
        _chkKeepRecordingCalls.Checked = s.AutoPause.KeepRecordingDuringCalls;
        _chkKeepRecordingSound.Checked = s.AutoPause.KeepRecordingWhileSoundPlays;
        _chkResumeWhenBack.Checked = s.AutoPause.ResumeWhenBack;
        _chkPauseWhenLocked.Checked = s.AutoPause.PauseWhenLocked;
        _chkPauseOnSleep.Checked = s.AutoPause.PauseOnSleep;

        _numAwayMinutes.Enabled = _chkPauseWhenAway.Checked;
        _chkKeepRecordingCalls.Enabled = _chkPauseWhenAway.Checked;
        _chkKeepRecordingSound.Enabled = _chkPauseWhenAway.Checked;
        _chkResumeWhenBack.Enabled = _chkPauseWhenAway.Checked;

        _cmbPausedReminder.SelectedIndex = SelectValue(_cmbPausedReminder, _pausedReminderValues, s.AutoPause.PausedReminderMinutes,
            mins => mins == 0 ? "Off" : $"{mins} minutes");
    }

    private void SavePauseResumeSettings(AppSettings s)
    {
        s.AutoPause.PauseWhenAway = _chkPauseWhenAway.Checked;
        s.AutoPause.AwayMinutes = (int)_numAwayMinutes.Value;
        s.AutoPause.KeepRecordingDuringCalls = _chkKeepRecordingCalls.Checked;
        s.AutoPause.KeepRecordingWhileSoundPlays = _chkKeepRecordingSound.Checked;
        s.AutoPause.ResumeWhenBack = _chkResumeWhenBack.Checked;
        s.AutoPause.PauseWhenLocked = _chkPauseWhenLocked.Checked;
        s.AutoPause.PauseOnSleep = _chkPauseOnSleep.Checked;
        s.AutoPause.PausedReminderMinutes = ValueAt(_pausedReminderValues, _cmbPausedReminder.SelectedIndex, 10);
    }
}
