using System.Drawing;
using System.Windows.Forms;
using ScreenVault.App.Platform;
using ScreenVault.Core.SystemIntegration;

namespace ScreenVault.App.UI;

public sealed class ReminderPromptForm : Form
{
    private readonly Action _onStartRecording;
    private readonly IReminderService _reminderService;
    private readonly System.Windows.Forms.Timer _autoDismissTimer;
    private int _remainingSeconds = 30;

    private readonly Label _lblPrompt;
    private readonly Button _btnStart;
    private readonly Button _btnSnooze1H;
    private readonly Button _btnNotToday;
    private readonly Button _btnDismiss;

    public ReminderPromptForm(
        Action onStartRecording,
        IReminderService reminderService)
    {
        _onStartRecording = onStartRecording ?? throw new ArgumentNullException(nameof(onStartRecording));
        _reminderService = reminderService ?? throw new ArgumentNullException(nameof(reminderService));

        _autoDismissTimer = new System.Windows.Forms.Timer { Interval = 1000 };

        Text = "ScreenVault Reminder";
        var appIcon = AppIcon.Get();
        if (appIcon != null) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(350, 140);

        _lblPrompt = new Label
        {
            Location = new Point(16, 16),
            Size = new Size(318, 45),
            Font = new Font("Segoe UI", 9.5f),
            Text = "ScreenVault is not recording.\nWould you like to start? (30s)",
            UseMnemonic = false
        };

        _btnStart = new Button
        {
            Location = new Point(16, 68),
            Size = new Size(110, 30),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            BackColor = Color.FromArgb(30, 142, 62),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Text = "● Start",
            UseMnemonic = false
        };
        _btnStart.FlatAppearance.BorderSize = 0;
        _btnStart.Click += (_, _) =>
        {
            _autoDismissTimer.Stop();
            Close();
            _onStartRecording();
        };

        _btnSnooze1H = new Button
        {
            Location = new Point(132, 68),
            Size = new Size(78, 30),
            Font = new Font("Segoe UI", 8.5f),
            Text = "Snooze 1h",
            UseMnemonic = false
        };
        _btnSnooze1H.Click += (_, _) =>
        {
            _autoDismissTimer.Stop();
            _reminderService.Snooze(TimeSpan.FromHours(1));
            Close();
        };

        _btnNotToday = new Button
        {
            Location = new Point(216, 68),
            Size = new Size(72, 30),
            Font = new Font("Segoe UI", 8.5f),
            Text = "Not today",
            UseMnemonic = false
        };
        _btnNotToday.Click += (_, _) =>
        {
            _autoDismissTimer.Stop();
            _reminderService.SnoozeForToday();
            Close();
        };

        _btnDismiss = new Button
        {
            Location = new Point(292, 68),
            Size = new Size(42, 30),
            Font = new Font("Segoe UI", 8.5f),
            Text = "✕",
            UseMnemonic = false
        };
        _btnDismiss.Click += (_, _) =>
        {
            _autoDismissTimer.Stop();
            Close();
        };

        Controls.Add(_lblPrompt);
        Controls.Add(_btnStart);
        Controls.Add(_btnSnooze1H);
        Controls.Add(_btnNotToday);
        Controls.Add(_btnDismiss);

        PositionAtBottomRight();

        _autoDismissTimer.Tick += (_, _) =>
        {
            _remainingSeconds--;
            if (_remainingSeconds <= 0)
            {
                _autoDismissTimer.Stop();
                Close();
            }
            else
            {
                _lblPrompt.Text = $"ScreenVault is not recording.\nWould you like to start? ({_remainingSeconds}s)";
            }
        };
        _autoDismissTimer.Start();
    }

    private void PositionAtBottomRight()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(this);
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoDismissTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
