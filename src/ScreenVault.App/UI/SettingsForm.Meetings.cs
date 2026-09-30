using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Settings;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private readonly ModernComboBox _cmbMeetingMode = new();
    private readonly ToggleSwitch _chkPromptStopMeeting = new();
    private readonly ToggleSwitch _chkAnyApp = new();
    private readonly StringListEditor _edtWatchedApps = new("Add process name (e.g. teams.exe)...");
    private readonly StringListEditor _edtAutoStartApps = new("Add process name to always auto-start (e.g. zoom.exe)...");

    // Reminders
    private readonly ToggleSwitch _chkReminderEnabled = new();
    private readonly TextField _txtWorkStart = new() { PlaceholderText = "09:00" };
    private readonly TextField _txtWorkEnd = new() { PlaceholderText = "18:00" };
    private readonly NumberField _numRemindAfter = new() { Minimum = 1, Maximum = 120, Value = 15, Suffix = "min" };
    private readonly NumberField _numRepeatEvery = new() { Minimum = 5, Maximum = 120, Value = 30, Suffix = "min" };
    private readonly CheckBox[] _chkWorkDays = new CheckBox[7];
    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    private StackPanel BuildMeetingsPage()
    {
        _cmbMeetingMode.Items.AddRange(["Ask me (recommended)", "Start recording automatically", "Do nothing"]);
        _cmbMeetingMode.Width = 260;
        _txtWorkStart.Width = 100;
        _txtWorkEnd.Width = 100;
        _numRemindAfter.Width = 110;
        _numRepeatEvery.Width = 110;

        var page = CreatePage("Meetings", "Automatic call detection, call apps, and scheduled workday recording reminders.");

        page.Controls.Add(Section("Call detection"));
        page.Controls.Add(Card(
            Row("When a call starts", "Teams, Zoom, Slack, Discord or a browser starts using the microphone while you're not recording.", _cmbMeetingMode, Glyphs.Headphones),
            Row("Offer to stop when the call ends", "Only for recordings that were started because of that call.", _chkPromptStopMeeting, Glyphs.Stop),
            Row("Any app using microphone counts", "Treat any app accessing the microphone as a meeting, even if not listed below.", _chkAnyApp, Glyphs.Microphone)));

        page.Controls.Add(Section("Apps that count as calls"));
        var cardWatched = new CardPanel { Padding = new Padding(12), Spacing = 8 };
        cardWatched.Controls.Add(new TextLabel("ScreenVault detects microphone use by these processes:", Typography.Caption, TextTone.Secondary));
        _edtWatchedApps.Height = 150;
        cardWatched.Controls.Add(_edtWatchedApps);
        page.Controls.Add(cardWatched);

        page.Controls.Add(Section("Always auto-start for these apps"));
        var cardAuto = new CardPanel { Padding = new Padding(12), Spacing = 8 };
        cardAuto.Controls.Add(new TextLabel("Start recording immediately without asking when these specific apps use the microphone:", Typography.Caption, TextTone.Secondary));
        _edtAutoStartApps.Height = 120;
        cardAuto.Controls.Add(_edtAutoStartApps);
        page.Controls.Add(cardAuto);

        page.Controls.Add(Section("Workday recording reminders"));
        var dayFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
        for (var i = 0; i < 7; i++)
        {
            _chkWorkDays[i] = new CheckBox
            {
                Text = DayNames[i],
                AutoSize = true,
                Margin = new Padding(0, 0, 12, 0),
                Font = Typography.Body
            };
            dayFlow.Controls.Add(_chkWorkDays[i]);
        }

        var workHoursPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        workHoursPanel.Controls.Add(_txtWorkStart);
        workHoursPanel.Controls.Add(new TextLabel(" to ", Typography.Body) { Margin = new Padding(4, 6, 4, 0) });
        workHoursPanel.Controls.Add(_txtWorkEnd);

        _chkReminderEnabled.CheckedChanged += (_, _) =>
        {
            var en = _chkReminderEnabled.Checked;
            foreach (var chk in _chkWorkDays) chk.Enabled = en;
            _txtWorkStart.Enabled = en;
            _txtWorkEnd.Enabled = en;
            _numRemindAfter.Enabled = en;
            _numRepeatEvery.Enabled = en;
        };

        page.Controls.Add(Card(
            Row("Enable workday reminders", "Notify you to start recording during working hours if you haven't started.", _chkReminderEnabled, Glyphs.Clock),
            Row("Active workdays", null, dayFlow),
            Row("Working hours (HH:mm)", "E.g. 09:00 to 18:00", workHoursPanel),
            Row("Remind after unrecorded work", "Notify after this many minutes of activity without recording.", _numRemindAfter),
            Row("Repeat reminder every", "Repeat notification if still not recording.", _numRepeatEvery)));

        return page;
    }

    private void LoadMeetingsSettings(AppSettings s)
    {
        _cmbMeetingMode.SelectedIndex = s.MeetingDetection.Mode switch
        {
            MeetingDetectionMode.AutoStart => 1,
            MeetingDetectionMode.Off => 2,
            _ => 0
        };
        _chkPromptStopMeeting.Checked = s.MeetingDetection.PromptStopWhenMeetingEnds;
        _chkAnyApp.Checked = s.MeetingDetection.AnyApp;
        _edtWatchedApps.SetItems(s.MeetingDetection.WatchedApps);
        _edtAutoStartApps.SetItems(s.MeetingDetection.AutoStartApps);

        _chkReminderEnabled.Checked = s.Reminders.Enabled;
        var workDays = new HashSet<string>(s.Reminders.WorkDays, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 7; i++)
        {
            _chkWorkDays[i].Checked = workDays.Contains(DayNames[i]);
        }

        _txtWorkStart.Text = s.Reminders.WorkStart;
        _txtWorkEnd.Text = s.Reminders.WorkEnd;
        _numRemindAfter.Value = Math.Clamp(s.Reminders.RemindAfterMinutes, 1, 120);
        _numRepeatEvery.Value = Math.Clamp(s.Reminders.RepeatEveryMinutes, 5, 120);

        var en = _chkReminderEnabled.Checked;
        foreach (var chk in _chkWorkDays) chk.Enabled = en;
        _txtWorkStart.Enabled = en;
        _txtWorkEnd.Enabled = en;
        _numRemindAfter.Enabled = en;
        _numRepeatEvery.Enabled = en;
    }

    private void SaveMeetingsSettings(AppSettings s)
    {
        s.MeetingDetection.Mode = _cmbMeetingMode.SelectedIndex switch
        {
            1 => MeetingDetectionMode.AutoStart,
            2 => MeetingDetectionMode.Off,
            _ => MeetingDetectionMode.Ask
        };
        s.MeetingDetection.PromptStopWhenMeetingEnds = _chkPromptStopMeeting.Checked;
        s.MeetingDetection.AnyApp = _chkAnyApp.Checked;
        s.MeetingDetection.WatchedApps = _edtWatchedApps.GetItems();
        s.MeetingDetection.AutoStartApps = _edtAutoStartApps.GetItems();

        s.Reminders.Enabled = _chkReminderEnabled.Checked;
        s.Reminders.WorkDays = DayNames.Where((_, idx) => _chkWorkDays[idx].Checked).ToList();
        s.Reminders.WorkStart = string.IsNullOrWhiteSpace(_txtWorkStart.Text) ? "09:00" : _txtWorkStart.Text.Trim();
        s.Reminders.WorkEnd = string.IsNullOrWhiteSpace(_txtWorkEnd.Text) ? "18:00" : _txtWorkEnd.Text.Trim();
        s.Reminders.RemindAfterMinutes = (int)_numRemindAfter.Value;
        s.Reminders.RepeatEveryMinutes = (int)_numRepeatEvery.Value;
    }
}
