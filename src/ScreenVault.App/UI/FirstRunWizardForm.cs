using System.Diagnostics;
using System.Media;
using ScreenVault.App.Platform;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

public sealed class FirstRunWizardForm : Form
{
    private readonly ISettingsService _settingsService;
    private readonly IAudioEngine _audioEngine;
    private int _currentStep = 1;
    private const int TotalSteps = 5;

    private readonly Panel _panelContent;
    private readonly Button _btnBack;
    private readonly Button _btnNext;
    private readonly Button _btnCancel;
    private readonly Label _lblStepIndicator;

    // Step 2 controls
    private TextBox? _txtPrimaryStorage;
    private TextBox? _txtBackupStorage;

    // Step 3 controls
    private VuMeterControl? _vuMic;
    private VuMeterControl? _vuOut;
    private System.Windows.Forms.Timer? _vuTimer;

    // Step 4 controls
    private CheckBox? _chkStartWithWindows;
    private CheckBox? _chkAutoStart;
    private CheckBox? _chkRetention;
    private NumericUpDown? _numRetentionDays;

    public FirstRunWizardForm(ISettingsService settingsService, IAudioEngine audioEngine)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));

        Text = "ScreenVault Setup Wizard";
        var appIcon = AppIcon.Get();
        if (appIcon != null) Icon = appIcon;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(540, 420);

        _lblStepIndicator = new Label
        {
            Location = new Point(20, 14),
            Size = new Size(500, 20),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(100, 100, 100),
            UseMnemonic = false
        };

        _panelContent = new Panel
        {
            Location = new Point(20, 40),
            Size = new Size(500, 320)
        };

        _btnBack = new Button
        {
            Text = "◀ Back",
            Location = new Point(260, 375),
            Size = new Size(80, 30),
            Enabled = false,
            UseMnemonic = false
        };
        _btnBack.Click += (_, _) => NavigateStep(-1);

        _btnNext = new Button
        {
            Text = "Next ▶",
            Location = new Point(350, 375),
            Size = new Size(80, 30),
            UseMnemonic = false
        };
        _btnNext.Click += (_, _) => NavigateStep(1);

        _btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(440, 375),
            Size = new Size(80, 30),
            DialogResult = DialogResult.Cancel,
            UseMnemonic = false
        };

        Controls.AddRange([_lblStepIndicator, _panelContent, _btnBack, _btnNext, _btnCancel]);

        ShowStep(1);
    }

    private void NavigateStep(int delta)
    {
        var target = _currentStep + delta;
        if (target < 1) return;

        if (target > TotalSteps)
        {
            SaveAndFinish();
            return;
        }

        ShowStep(target);
    }

    private void ShowStep(int step)
    {
        _currentStep = step;
        _lblStepIndicator.Text = $"Step {step} of {TotalSteps}";
        _btnBack.Enabled = step > 1;
        _btnNext.Text = step == TotalSteps ? "Finish" : "Next ▶";

        _panelContent.Controls.Clear();
        StopVuTimer();

        switch (step)
        {
            case 1:
                BuildWelcomeStep();
                break;
            case 2:
                BuildStorageStep();
                break;
            case 3:
                BuildAudioCheckStep();
                break;
            case 4:
                BuildStartupRetentionStep();
                break;
            case 5:
                BuildFinishStep();
                break;
        }
    }

    private void BuildWelcomeStep()
    {
        var title = new Label
        {
            Text = "Welcome to ScreenVault",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            Location = new Point(0, 10),
            Size = new Size(500, 32)
        };

        var desc = new Label
        {
            Text = "ScreenVault is an always-on, crash-proof recorder that captures your screen, microphone, and meeting audio directly to your drive.\n\n" +
                   "Key principles:\n" +
                   "• Crash-Proof: Video is written in 1-second durable increments. If your PC crashes or loses power, your recording is safe.\n" +
                   "• Always Visible: A red dot in your system tray means recording is active. There is no hidden mode.\n" +
                   "• 100% Offline: ScreenVault never makes network requests or uploads your data.\n\n" +
                   "Let's configure your storage, test your audio, and get started in just a few clicks.",
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(0, 50),
            Size = new Size(500, 240)
        };

        _panelContent.Controls.AddRange([title, desc]);
    }

    private void BuildStorageStep()
    {
        var title = new Label
        {
            Text = "Storage Locations",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Location = new Point(0, 10),
            Size = new Size(500, 26)
        };

        var desc = new Label
        {
            Text = "Choose where your recordings will be saved. If your primary drive runs low on space, ScreenVault automatically fails over to your backup location.",
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(0, 40),
            Size = new Size(500, 40)
        };

        var currentLocs = _settingsService.Current.Storage.Locations;
        var primaryPath = currentLocs.Count > 0 ? currentLocs[0].Path : @"%USERPROFILE%\Videos\Screen Recordings";
        var backupPath = currentLocs.Count > 1 ? currentLocs[1].Path : @"D:\ScreenVault Backup";

        var lblPrimary = new Label { Text = "Primary Storage Path:", Location = new Point(0, 95), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _txtPrimaryStorage = new TextBox { Text = primaryPath, Location = new Point(0, 118), Width = 400 };
        var btnBrowsePrimary = new Button { Text = "Browse…", Location = new Point(410, 116), Width = 80, Height = 26 };
        btnBrowsePrimary.Click += (_, _) => BrowseFolder(_txtPrimaryStorage);

        var lblBackup = new Label { Text = "Backup Storage Path (Failover):", Location = new Point(0, 160), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _txtBackupStorage = new TextBox { Text = backupPath, Location = new Point(0, 183), Width = 400 };
        var btnBrowseBackup = new Button { Text = "Browse…", Location = new Point(410, 181), Width = 80, Height = 26 };
        btnBrowseBackup.Click += (_, _) => BrowseFolder(_txtBackupStorage);

        _panelContent.Controls.AddRange([title, desc, lblPrimary, _txtPrimaryStorage, btnBrowsePrimary, lblBackup, _txtBackupStorage, btnBrowseBackup]);
    }

    private void BrowseFolder(TextBox target)
    {
        using var fbd = new FolderBrowserDialog();
        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = fbd.SelectedPath;
        }
    }

    private void BuildAudioCheckStep()
    {
        var title = new Label
        {
            Text = "Audio Verification",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Location = new Point(0, 5),
            Size = new Size(500, 24)
        };

        var desc = new Label
        {
            Text = "Speak into your microphone and play a test chime to verify both audio streams are alive.",
            Font = new Font("Segoe UI", 9f),
            Location = new Point(0, 32),
            Size = new Size(500, 20)
        };

        var lblMic = new Label { Text = "Microphone (Say something):", Location = new Point(0, 60), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _vuMic = new VuMeterControl { Location = new Point(0, 82), Size = new Size(490, 24) };

        var lblOut = new Label { Text = "System Audio (Speakers / Headphones):", Location = new Point(0, 120), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _vuOut = new VuMeterControl { Location = new Point(0, 142), Size = new Size(490, 24) };

        var btnTestChime = new Button
        {
            Text = "▶ Play Test Chime",
            Location = new Point(0, 180),
            Size = new Size(140, 30)
        };
        btnTestChime.Click += (_, _) =>
        {
            try { SystemSounds.Asterisk.Play(); } catch { }
        };

        var btnPrivacy = new Button
        {
            Text = "Windows Mic Privacy Settings…",
            Location = new Point(155, 180),
            Size = new Size(210, 30)
        };
        btnPrivacy.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); } catch { }
        };

        _panelContent.Controls.AddRange([title, desc, lblMic, _vuMic, lblOut, _vuOut, btnTestChime, btnPrivacy]);

        // Start live VU timer at 10 Hz
        _vuTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _vuTimer.Tick += (_, _) =>
        {
            try
            {
                var status = _audioEngine.GetStatus();
                var mic = status.ActiveDevices.FirstOrDefault(d => !d.IsLoopback);
                var sys = status.ActiveDevices.FirstOrDefault(d => d.IsLoopback);
                _vuMic?.SetLevels(mic?.PeakDb ?? -90f, mic?.PeakDb ?? -90f, mic?.RmsDb ?? -90f, mic?.RmsDb ?? -90f);
                _vuOut?.SetLevels(sys?.PeakDb ?? -90f, sys?.PeakDb ?? -90f, sys?.RmsDb ?? -90f, sys?.RmsDb ?? -90f);
            }
            catch
            {
                // Ignore
            }
        };
        _audioEngine.SetMonitoring(true);
        _vuTimer.Start();
    }

    private void BuildStartupRetentionStep()
    {
        var title = new Label
        {
            Text = "Startup && Retention",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Location = new Point(0, 10),
            Size = new Size(500, 26),
            UseMnemonic = false
        };

        _chkStartWithWindows = new CheckBox
        {
            Text = "Start ScreenVault automatically when Windows starts (Recommended)",
            Location = new Point(0, 48),
            Size = new Size(490, 24),
            Checked = _settingsService.Current.General.StartWithWindows,
            UseMnemonic = false
        };

        _chkAutoStart = new CheckBox
        {
            Text = "Start recording immediately on launch",
            Location = new Point(0, 80),
            Size = new Size(490, 24),
            Checked = _settingsService.Current.General.StartRecordingOnLaunch
        };

        _chkRetention = new CheckBox
        {
            Text = "Enable automatic cleanup of old recordings",
            Location = new Point(0, 120),
            Size = new Size(490, 24),
            Checked = _settingsService.Current.Storage.Retention.Enabled
        };

        var lblKeep = new Label { Text = "Keep recordings for (days):", Location = new Point(24, 155), AutoSize = true };
        _numRetentionDays = new NumericUpDown
        {
            Location = new Point(190, 153),
            Width = 70,
            Minimum = 1,
            Maximum = 365,
            Value = Math.Max(1, _settingsService.Current.Storage.Retention.KeepDays)
        };

        _panelContent.Controls.AddRange([title, _chkStartWithWindows, _chkAutoStart, _chkRetention, lblKeep, _numRetentionDays]);
    }

    private void BuildFinishStep()
    {
        var title = new Label
        {
            Text = "You're Ready to Record!",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            Location = new Point(0, 10),
            Size = new Size(500, 32)
        };

        var desc = new Label
        {
            Text = "Configuration is complete!\n\n" +
                   "• Hotkey Ctrl+Alt+Shift+M: Add a meeting marker note.\n" +
                   "• Hotkey Ctrl+Alt+Shift+P: Pause or resume recording.\n" +
                   "• Hotkey Ctrl+Alt+Shift+S: Toggle the Status window.\n\n" +
                   "Click 'Finish' to save your settings and begin recording.",
            Font = new Font("Segoe UI", 10f),
            Location = new Point(0, 60),
            Size = new Size(500, 200)
        };

        _panelContent.Controls.AddRange([title, desc]);
    }

    private void StopVuTimer()
    {
        _audioEngine?.SetMonitoring(false);
        if (_vuTimer != null)
        {
            _vuTimer.Stop();
            _vuTimer.Dispose();
            _vuTimer = null;
        }
    }

    private void SaveAndFinish()
    {
        try
        {
            var settings = _settingsService.Current;

            if (_txtPrimaryStorage != null && !string.IsNullOrWhiteSpace(_txtPrimaryStorage.Text))
            {
                if (settings.Storage.Locations.Count > 0)
                {
                    settings.Storage.Locations[0].Path = _txtPrimaryStorage.Text.Trim();
                }
            }

            if (_txtBackupStorage != null && !string.IsNullOrWhiteSpace(_txtBackupStorage.Text))
            {
                if (settings.Storage.Locations.Count > 1)
                {
                    settings.Storage.Locations[1].Path = _txtBackupStorage.Text.Trim();
                }
                else
                {
                    settings.Storage.Locations.Add(new StorageLocationConfig
                    {
                        Path = _txtBackupStorage.Text.Trim(),
                        MinFreeGb = 5,
                        Enabled = true
                    });
                }
            }

            if (_chkAutoStart != null)
            {
                settings.General.StartRecordingOnLaunch = _chkAutoStart.Checked;
            }

            if (_chkStartWithWindows != null)
            {
                settings.General.StartWithWindows = _chkStartWithWindows.Checked;
                try { StartWithWindows.SetEnabled(settings.General.StartWithWindows, settings.General.StartRecordingOnLaunch); } catch { }
            }

            if (_chkRetention != null)
            {
                settings.Storage.Retention.Enabled = _chkRetention.Checked;
            }

            if (_numRetentionDays != null)
            {
                settings.Storage.Retention.KeepDays = (int)_numRetentionDays.Value;
            }

            _settingsService.Save(settings);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save wizard settings.");
            MessageBox.Show(this, "Could not save settings: " + ex.Message, "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopVuTimer();
        base.OnFormClosing(e);
    }
}
