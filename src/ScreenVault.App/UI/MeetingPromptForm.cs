using System.Drawing;
using System.Windows.Forms;
using ScreenVault.App.Platform;
using ScreenVault.Core.Settings;

namespace ScreenVault.App.UI;

public sealed class MeetingPromptForm : Form
{
    private readonly Action _onStartRecording;
    private readonly Action<string> _onAlwaysForApp;
    private readonly string _appName;
    private readonly System.Windows.Forms.Timer _autoDismissTimer;
    private int _remainingSeconds = 30;

    private readonly Label _lblPrompt;
    private readonly Button _btnStart;
    private readonly Button _btnNotNow;
    private readonly Button _btnAlways;

    public MeetingPromptForm(
        string appName,
        Action onStartRecording,
        Action<string> onAlwaysForApp)
    {
        _appName = appName;
        _onStartRecording = onStartRecording ?? throw new ArgumentNullException(nameof(onStartRecording));
        _onAlwaysForApp = onAlwaysForApp ?? throw new ArgumentNullException(nameof(onAlwaysForApp));

        Text = "ScreenVault – Meeting Detected";
        var appIcon = AppIcon.Get();
        if (appIcon != null) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(340, 140);

        _autoDismissTimer = new System.Windows.Forms.Timer { Interval = 1000 };

        _lblPrompt = new Label
        {
            Location = new Point(16, 16),
            Size = new Size(308, 45),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            Text = $"{_appName} is using your microphone.\nStart recording? (30s)",
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

        _btnNotNow = new Button
        {
            Location = new Point(134, 68),
            Size = new Size(80, 30),
            Font = new Font("Segoe UI", 9f),
            Text = "Not Now",
            UseMnemonic = false
        };
        _btnNotNow.Click += (_, _) =>
        {
            _autoDismissTimer.Stop();
            Close();
        };

        _btnAlways = new Button
        {
            Location = new Point(222, 68),
            Size = new Size(102, 30),
            Font = new Font("Segoe UI", 8.5f),
            Text = $"Always ({_appName})",
            UseMnemonic = false
        };
        _btnAlways.Click += (_, _) =>
        {
            _autoDismissTimer.Stop();
            Close();
            _onAlwaysForApp(_appName);
            _onStartRecording();
        };

        Controls.Add(_lblPrompt);
        Controls.Add(_btnStart);
        Controls.Add(_btnNotNow);
        Controls.Add(_btnAlways);

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
                _lblPrompt.Text = $"{_appName} is using your microphone.\nStart recording? ({_remainingSeconds}s)";
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
