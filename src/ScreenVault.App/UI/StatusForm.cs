using System.Diagnostics;
using System.Globalization;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenVault.App.Platform;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Serilog;

namespace ScreenVault.App.UI;

public sealed class StatusForm : Form
{
    private readonly IRecordingController _controller;
    private readonly IAudioEngine _audioEngine;
    private readonly IStorageManager? _storageManager;
    private readonly IMarkerService? _markerService;
    private readonly ISettingsService _settingsService;
    private readonly PlayerLauncher _playerLauncher;
    private readonly Action _openSettingsAction;

    private readonly System.Windows.Forms.Timer _timer10Hz;
    private readonly System.Windows.Forms.Timer _timer1Hz;

    private readonly Label _lblState;
    private readonly Label _lblElapsed;
    private readonly Label _lblPartElapsed;
    private readonly Label _lblDegraded;
    private readonly CheckBox _chkPin;

    private readonly Label _lblFile;
    private readonly Button _btnPlay;
    private readonly ContextMenuStrip _playMenu;

    private readonly FlowLayoutPanel _pnlStorage;
    private readonly Label _lblMicName;
    private readonly VuMeterControl _vuMic;
    private readonly Label _lblOutName;
    private readonly VuMeterControl _vuOut;
    private readonly Label _lblAudioSwitch;
    private readonly Button _btnTestAudio;

    private readonly Label _lblVideoStats;
    private readonly Button _btnMain; // Start Recording / Stop & Save
    private readonly Button _btnPauseResume;
    private readonly Button _btnAddMarker;
    private readonly Button _btnOpenFolder;
    private readonly Button _btnSettings;
    private readonly ToolTip _toolTip = new();

    private bool _isPinned;
    private bool _isTestingAudio;
    private bool _isStartupComplete;

    public void SetStartupComplete(bool complete = true)
    {
        _isStartupComplete = complete;
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(Refresh1Hz);
        }
    }

    public StatusForm(
        IRecordingController controller,
        IAudioEngine audioEngine,
        IStorageManager? storageManager,
        IMarkerService? markerService,
        ISettingsService settingsService,
        Action openSettingsAction,
        PlayerLauncher? playerLauncher = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        _storageManager = storageManager;
        _markerService = markerService;
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _openSettingsAction = openSettingsAction ?? throw new ArgumentNullException(nameof(openSettingsAction));
        _playerLauncher = playerLauncher ?? new PlayerLauncher(_settingsService);

        Text = "ScreenVault Status";
        var appIcon = AppIcon.Get();
        if (appIcon != null) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(390, 560);
        TopMost = true;

        // Header: State & Time
        _lblState = new Label
        {
            Location = new Point(14, 12),
            Size = new Size(190, 24),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Text = "● Ready",
            ForeColor = Color.FromArgb(30, 142, 62),
            UseMnemonic = false
        };

        _lblElapsed = new Label
        {
            Location = new Point(205, 12),
            Size = new Size(130, 24),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            TextAlign = ContentAlignment.TopRight,
            Text = "00:00:00",
            UseMnemonic = false
        };

        _lblPartElapsed = new Label
        {
            Location = new Point(205, 36),
            Size = new Size(130, 16),
            Font = new Font("Segoe UI", 8f),
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.TopRight,
            Text = string.Empty,
            UseMnemonic = false
        };

        _chkPin = new CheckBox
        {
            Appearance = Appearance.Button,
            Text = "\U0001F4CC",
            Location = new Point(345, 10),
            Size = new Size(30, 28),
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false
        };
        _chkPin.CheckedChanged += (_, _) =>
        {
            _isPinned = _chkPin.Checked;
            TopMost = _isPinned;
        };

        _lblDegraded = new Label
        {
            Location = new Point(16, 38),
            Size = new Size(200, 18),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.DarkOrange,
            Text = string.Empty,
            UseMnemonic = false
        };

        // Group 1: Current Recording File
        var grpFile = new GroupBox
        {
            Text = "Current Recording",
            Location = new Point(14, 58),
            Size = new Size(362, 70),
            Font = new Font("Segoe UI", 8.5f)
        };

        _lblFile = new Label
        {
            Location = new Point(10, 20),
            Size = new Size(260, 42),
            Cursor = Cursors.Hand,
            Text = "Ready to record",
            UseMnemonic = false
        };
        _lblFile.Click += (_, _) => RevealCurrentFile();

        // Split button ▶ Play ▾
        _btnPlay = new Button
        {
            Text = "▶ Play ▾",
            Location = new Point(275, 24),
            Size = new Size(78, 30),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            UseMnemonic = false
        };
        _toolTip.SetToolTip(_btnPlay, "Play Recording (▶)");
        _playMenu = new ContextMenuStrip();
        _playMenu.Items.Add("Play Current Part", null, (_, _) => PlayCurrentFile());
        _playMenu.Items.Add("Play Last Saved File", null, (_, _) => PlayLastSavedFile());
        _playMenu.Items.Add(new ToolStripSeparator());
        _playMenu.Items.Add("Open With…", null, (_, _) => OpenWithDialog());
        _btnPlay.Click += (_, _) =>
        {
            if (_controller.State is RecorderState.Recording or RecorderState.Paused)
            {
                PlayCurrentFile();
            }
            else
            {
                PlayLastSavedFile();
            }
        };
        _btnPlay.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Right || (e.Button == MouseButtons.Left && e.X > _btnPlay.Width - 20))
            {
                _playMenu.Show(_btnPlay, 0, _btnPlay.Height);
            }
        };

        grpFile.Controls.Add(_lblFile);
        grpFile.Controls.Add(_btnPlay);

        // Group 2: Audio Levels
        var grpAudio = new GroupBox
        {
            Text = "Audio Levels",
            Location = new Point(14, 134),
            Size = new Size(362, 142),
            Font = new Font("Segoe UI", 8.5f)
        };

        _lblMicName = new Label { Location = new Point(10, 22), Size = new Size(170, 18), Text = "Mic: ✔ Loading…", UseMnemonic = false };
        _vuMic = new VuMeterControl { Location = new Point(185, 20), Width = 168 };

        _lblOutName = new Label { Location = new Point(10, 52), Size = new Size(170, 18), Text = "System: ✔ Loading…", UseMnemonic = false };
        _vuOut = new VuMeterControl { Location = new Point(185, 50), Width = 168 };

        _lblAudioSwitch = new Label
        {
            Location = new Point(10, 80),
            Size = new Size(342, 22),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 7.5f),
            Text = "All audio sources active",
            UseMnemonic = false
        };

        _btnTestAudio = new Button
        {
            Text = "Test Audio",
            Location = new Point(10, 106),
            Size = new Size(90, 26),
            Font = new Font("Segoe UI", 8f),
            UseMnemonic = false
        };
        _btnTestAudio.Click += async (_, _) => await RunAudioTestAsync();
        _toolTip.SetToolTip(_btnTestAudio, "Test Audio (Ready state only)");

        grpAudio.Controls.Add(_lblMicName);
        grpAudio.Controls.Add(_vuMic);
        grpAudio.Controls.Add(_lblOutName);
        grpAudio.Controls.Add(_vuOut);
        grpAudio.Controls.Add(_lblAudioSwitch);
        grpAudio.Controls.Add(_btnTestAudio);

        // Group 3: Storage
        var grpStorage = new GroupBox
        {
            Text = "Storage",
            Location = new Point(14, 282),
            Size = new Size(362, 90),
            Font = new Font("Segoe UI", 8.5f)
        };
        _pnlStorage = new FlowLayoutPanel
        {
            Location = new Point(8, 18),
            Size = new Size(346, 64),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        grpStorage.Controls.Add(_pnlStorage);

        // Group 4: Video
        var grpVideo = new GroupBox
        {
            Text = "Video && Encoder",
            Location = new Point(14, 378),
            Size = new Size(362, 60),
            Font = new Font("Segoe UI", 8.5f)
        };
        _lblVideoStats = new Label
        {
            Location = new Point(10, 20),
            Size = new Size(342, 32),
            Text = "Encoder: Loading...",
            UseMnemonic = false
        };
        grpVideo.Controls.Add(_lblVideoStats);

        // Action Buttons Row 1: Main Start / Stop & Save & Pause
        _btnMain = new Button
        {
            Text = "Preparing…",
            Location = new Point(14, 448),
            Size = new Size(174, 36),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            BackColor = Color.FromArgb(107, 107, 107),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false,
            UseMnemonic = false
        };
        _btnMain.FlatAppearance.BorderSize = 0;
        _btnMain.Click += async (_, _) => await OnMainButtonClickedAsync();
        _toolTip.SetToolTip(_btnMain, "Start Recording or Stop & Save (Ctrl+Alt+Shift+R)");

        _btnPauseResume = new Button
        {
            Text = "⏸ Pause",
            Location = new Point(194, 448),
            Size = new Size(182, 36),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            UseMnemonic = false
        };
        _btnPauseResume.Click += async (_, _) => await TogglePauseAsync();
        _toolTip.SetToolTip(_btnPauseResume, "Pause or Resume Recording (Ctrl+Alt+Shift+P)");

        // Action Buttons Row 2: Add Marker, Open Folder, Settings
        _btnAddMarker = new Button
        {
            Text = "Add Marker",
            Location = new Point(14, 492),
            Size = new Size(110, 32),
            UseMnemonic = false
        };
        _btnAddMarker.Click += (_, _) => ShowMarkerDialog();
        _toolTip.SetToolTip(_btnAddMarker, "Add Marker (Ctrl+Alt+Shift+M)");

        _btnOpenFolder = new Button
        {
            Text = "Open Folder",
            Location = new Point(130, 492),
            Size = new Size(116, 32),
            UseMnemonic = false
        };
        _btnOpenFolder.Click += (_, _) => OpenRecordingsFolder();
        _toolTip.SetToolTip(_btnOpenFolder, "Open Folder (Ctrl+Alt+Shift+O)");

        _btnSettings = new Button
        {
            Text = "Settings…",
            Location = new Point(252, 492),
            Size = new Size(124, 32),
            UseMnemonic = false
        };
        _btnSettings.Click += (_, _) => _openSettingsAction();
        _toolTip.SetToolTip(_btnSettings, "Settings…");

        Controls.Add(_lblState);
        Controls.Add(_lblElapsed);
        Controls.Add(_lblPartElapsed);
        Controls.Add(_chkPin);
        Controls.Add(_lblDegraded);
        Controls.Add(grpFile);
        Controls.Add(grpAudio);
        Controls.Add(grpStorage);
        Controls.Add(grpVideo);
        Controls.Add(_btnMain);
        Controls.Add(_btnPauseResume);
        Controls.Add(_btnAddMarker);
        Controls.Add(_btnOpenFolder);
        Controls.Add(_btnSettings);

        // Timers: 10 Hz (VU meters) and 1 Hz (stats)
        _timer10Hz = new System.Windows.Forms.Timer { Interval = 100 };
        _timer10Hz.Tick += OnTick10Hz;

        _timer1Hz = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer1Hz.Tick += OnTick1Hz;

        Deactivate += (_, _) =>
        {
            if (!_isPinned)
            {
                Hide();
            }
        };

        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                _audioEngine.SetMonitoring(true);
                _timer10Hz.Start();
                _timer1Hz.Start();
                Refresh1Hz();
            }
            else
            {
                _audioEngine.SetMonitoring(false);
                _timer10Hz.Stop();
                _timer1Hz.Stop();
            }
        };
    }

    public void AnchorNearTray()
    {
        var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        Left = screen.Right - Width - 16;
        Top = screen.Bottom - Height - 16;
    }

    private void OnTick10Hz(object? sender, EventArgs e)
    {
        var audioStatus = _audioEngine.GetStatus();
        var micSources = audioStatus.ActiveDevices.Where(d => !d.IsLoopback).ToList();
        var sysSources = audioStatus.ActiveDevices.Where(d => d.IsLoopback).ToList();

        var micPeak = micSources.Count > 0 ? micSources.Max(s => s.PeakDb) : -60f;
        var micRms = micSources.Count > 0 ? micSources.Max(s => s.RmsDb) : -60f;

        var sysPeak = sysSources.Count > 0 ? sysSources.Max(s => s.PeakDb) : -60f;
        var sysRms = sysSources.Count > 0 ? sysSources.Max(s => s.RmsDb) : -60f;

        _vuMic.IsMuted = audioStatus.IsMicMuted;
        _vuMic.SetLevels(micPeak, micPeak, micRms, micRms);
        _vuOut.SetLevels(sysPeak, sysPeak, sysRms, sysRms);
    }

    private void OnTick1Hz(object? sender, EventArgs e)
    {
        Refresh1Hz();
    }

    private void Refresh1Hz()
    {
        var health = _controller.Health;

        // State, Header color, & Elapsed
        _lblElapsed.Text = health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        if (health.PartElapsed > TimeSpan.Zero || health.State == RecorderState.Recording || health.State == RecorderState.Paused)
        {
            _lblPartElapsed.Text = $"Part {health.PartIndex} · {health.PartElapsed:mm\\:ss}";
        }
        else
        {
            _lblPartElapsed.Text = string.Empty;
        }

        switch (health.State)
        {
            case RecorderState.Recording:
                _lblState.Text = health.IsDegraded
                    ? $"● REC {health.Elapsed:hh\\:mm\\:ss} (Degraded)"
                    : $"● REC {health.Elapsed:hh\\:mm\\:ss}";
                _lblState.ForeColor = health.IsDegraded ? Color.Orange : Color.FromArgb(217, 48, 37);
                _btnMain.Text = "■ Stop && Save";
                _btnMain.BackColor = Color.FromArgb(217, 48, 37);
                _btnMain.Enabled = true;
                _btnPauseResume.Text = "⏸ Pause";
                _btnPauseResume.Enabled = true;
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Paused:
                _lblState.Text = "⏸ Paused";
                _lblState.ForeColor = Color.FromArgb(107, 107, 107);
                _btnMain.Text = "■ Stop && Save";
                _btnMain.BackColor = Color.FromArgb(217, 48, 37);
                _btnMain.Enabled = true;
                _btnPauseResume.Text = "▶ Resume";
                _btnPauseResume.Enabled = true;
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Faulted:
                _lblState.Text = "Not recording — retrying";
                _lblState.ForeColor = Color.FromArgb(217, 48, 37);
                _btnMain.Text = "● Retry Start";
                _btnMain.BackColor = Color.FromArgb(30, 142, 62);
                _btnMain.Enabled = true;
                _btnPauseResume.Enabled = false;
                _btnTestAudio.Enabled = true;
                break;

            case RecorderState.Starting:
                _lblState.Text = "Starting…";
                _lblState.ForeColor = Color.FromArgb(30, 142, 62);
                _btnMain.Text = "Starting…";
                _btnMain.Enabled = false;
                _btnPauseResume.Enabled = false;
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Saving:
            case RecorderState.Stopping:
                _lblState.Text = "Saving…";
                _lblState.ForeColor = Color.FromArgb(107, 107, 107);
                _btnMain.Text = "Saving…";
                _btnMain.Enabled = false;
                _btnPauseResume.Enabled = false;
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Recovering:
                _lblState.Text = "Recovering…";
                _lblState.ForeColor = Color.Orange;
                _btnMain.Enabled = false;
                _btnPauseResume.Enabled = false;
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Idle:
            default:
                _lblState.Text = "● Ready";
                _lblState.ForeColor = Color.FromArgb(30, 142, 62);
                if (!_isStartupComplete)
                {
                    _btnMain.Text = "Preparing…";
                    _btnMain.BackColor = Color.FromArgb(107, 107, 107);
                    _btnMain.Enabled = false;
                }
                else
                {
                    _btnMain.Text = "● Start Recording";
                    _btnMain.BackColor = Color.FromArgb(30, 142, 62);
                    _btnMain.Enabled = true;
                }
                _btnPauseResume.Text = "⏸ Pause";
                _btnPauseResume.Enabled = false;
                _btnTestAudio.Enabled = !_isTestingAudio && _isStartupComplete;
                break;
        }

        if (health.DegradedWarnings.Count > 0)
        {
            _lblDegraded.Text = string.Join("; ", health.DegradedWarnings);
        }
        else
        {
            _lblDegraded.Text = string.Empty;
        }

        // Current file display
        var hasCurrentFile = !string.IsNullOrEmpty(health.CurrentFilePath) && File.Exists(health.CurrentFilePath);
        var hasSavedFile = GetLastSavedFilePath() != null;

        if (!string.IsNullOrEmpty(health.CurrentFilePath))
        {
            var fileName = Path.GetFileName(health.CurrentFilePath);
            var mb = health.CurrentFileBytes / (1024.0 * 1024.0);
            _lblFile.Text = $"{fileName}\nSize: {mb:F1} MB";
            _btnPlay.Enabled = hasCurrentFile || hasSavedFile;
        }
        else
        {
            _lblFile.Text = "Ready to record";
            _btnPlay.Enabled = hasSavedFile;
        }

        // Audio devices and switch status
        var audioStatus = _audioEngine.GetStatus();
        var mic = audioStatus.ActiveDevices.FirstOrDefault(d => !d.IsLoopback);
        var sys = audioStatus.ActiveDevices.FirstOrDefault(d => d.IsLoopback);

        _lblMicName.Text = $"Mic: {Truncate(audioStatus.MicDisplayStatus, 22)}";
        _toolTip.SetToolTip(_lblMicName, $"Mic: {audioStatus.MicDisplayStatus}");
        _lblOutName.Text = $"System: {Truncate(audioStatus.SystemDisplayStatus, 22)}";
        _toolTip.SetToolTip(_lblOutName, $"System: {audioStatus.SystemDisplayStatus}");
        _lblAudioSwitch.Text = $"Last switch: {audioStatus.LastSwitchSummary}";
        _toolTip.SetToolTip(_lblAudioSwitch, audioStatus.LastSwitchSummary);

        // Storage line
        if (_storageManager != null)
        {
            var status = _storageManager.GetStatus();
            _pnlStorage.SuspendLayout();
            _pnlStorage.Controls.Clear();

            foreach (var loc in status.Locations)
            {
                var isLocActive = string.Equals(loc.Path, status.ActiveLocationPath, StringComparison.OrdinalIgnoreCase);
                var freeGb = loc.AvailableFreeBytes / (1024.0 * 1024.0 * 1024.0);
                var expanded = Environment.ExpandEnvironmentVariables(loc.Path);
                var stateStr = loc.State switch
                {
                    StorageLocationState.Healthy => "Healthy",
                    StorageLocationState.Low => "Low Space",
                    StorageLocationState.Failed => "Failed",
                    StorageLocationState.Disabled => "Disabled",
                    _ => "Healthy"
                };

                var rowPanel = new FlowLayoutPanel
                {
                    AutoSize = true,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    Margin = new Padding(0, 0, 0, 2)
                };

                var lbl = new Label
                {
                    AutoSize = true,
                    Text = $"{(isLocActive ? "★ " : "  ")}{expanded} — {freeGb:F1} GB free · {stateStr}",
                    ForeColor = isLocActive ? Color.Black : Color.DimGray,
                    Font = new Font("Segoe UI", 8f, isLocActive ? FontStyle.Bold : FontStyle.Regular),
                    UseMnemonic = false
                };
                rowPanel.Controls.Add(lbl);

                long totalBytes = 0;
                try
                {
                    var root = Path.GetPathRoot(expanded);
                    if (!string.IsNullOrEmpty(root))
                    {
                        var drive = new DriveInfo(root);
                        if (drive.IsReady) totalBytes = drive.TotalSize;
                    }
                }
                catch
                {
                    // Ignore drive query failure
                }

                if (isLocActive && totalBytes > 0)
                {
                    var usedPercent = Math.Clamp((int)((1.0 - (loc.AvailableFreeBytes / (double)totalBytes)) * 100), 0, 100);
                    var pbar = new ProgressBar
                    {
                        Width = 55,
                        Height = 10,
                        Minimum = 0,
                        Maximum = 100,
                        Value = usedPercent,
                        Margin = new Padding(4, 3, 0, 0)
                    };
                    rowPanel.Controls.Add(pbar);
                }

                _pnlStorage.Controls.Add(rowPanel);
            }
            _pnlStorage.ResumeLayout();
        }

        // Video stats with color indicator
        var targetFps = _settingsService.Current.Video.FrameRate;
        var actualFps = health.ActualFps > 0 ? health.ActualFps : targetFps;
        var speedColor = health.Speed > 0 && health.Speed < 0.97 ? "orange" : "normal";
        _lblVideoStats.Text = $"Encoder: {health.EncoderProfile} · {actualFps:F0} fps (target {targetFps}) · Speed: {health.Speed:F2}x";
        _lblVideoStats.ForeColor = speedColor == "orange" ? Color.DarkOrange : Color.Black;
    }

    private static string Truncate(string text, int max)
    {
        return text.Length <= max ? text : text[..max] + "…";
    }

    private async Task OnMainButtonClickedAsync()
    {
        if (_controller.State is RecorderState.Recording or RecorderState.Paused)
        {
            if (_settingsService.Current.General.ConfirmBeforeStop)
            {
                var confirm = MessageBox.Show(this, "Stop and save the recording?", "ScreenVault", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;
            }

            await _controller.StopAsync().ConfigureAwait(true);
        }
        else if (_controller.State is RecorderState.Idle or RecorderState.Faulted)
        {
            await _controller.StartAsync().ConfigureAwait(true);
        }
    }

    private async Task TogglePauseAsync()
    {
        if (_controller.State == RecorderState.Recording)
        {
            await _controller.PauseAsync().ConfigureAwait(true);
        }
        else if (_controller.State == RecorderState.Paused)
        {
            await _controller.ResumeAsync().ConfigureAwait(true);
        }
    }

    private void ShowMarkerDialog()
    {
        var elapsed = _controller.Health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        using var dlg = new MarkerNoteForm(elapsed);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _markerService?.AddMarker(dlg.NoteText, "User");
        }
    }

    private void RevealCurrentFile()
    {
        var path = _controller.Health.CurrentFilePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }

    private void PlayCurrentFile()
    {
        var path = _controller.Health.CurrentFilePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            _playerLauncher.Launch(path);
        }
        else
        {
            MessageBox.Show(this, "Current recording part is not yet available on disk.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void PlayLastSavedFile()
    {
        var latestFile = GetLastSavedFilePath();
        if (!string.IsNullOrEmpty(latestFile) && File.Exists(latestFile))
        {
            _playerLauncher.Launch(latestFile);
            return;
        }

        MessageBox.Show(this, "No saved recordings found in storage locations.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private string? GetLastSavedFilePath()
    {
        try
        {
            var recordingsDir = _settingsService.Current.Storage.Locations.FirstOrDefault(l => l.Enabled)?.Path
                ?? @"%USERPROFILE%\Videos\Screen Recordings";
            var expandedDir = Environment.ExpandEnvironmentVariables(recordingsDir);

            if (Directory.Exists(expandedDir))
            {
                return Directory.EnumerateFiles(expandedDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }
        catch
        {
            // Ignore storage scan error
        }

        return null;
    }

    private void OpenWithDialog()
    {
        var path = _controller.Health.CurrentFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = GetLastSavedFilePath();
        }

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            PlayerLauncher.OpenWithDialog(path);
        }
        else
        {
            MessageBox.Show(this, "No playable video file available to open.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OpenRecordingsFolder()
    {
        var path = _controller.Health.CurrentFilePath;
        var dir = !string.IsNullOrEmpty(path) ? Path.GetDirectoryName(path) : null;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            var primary = _settingsService.Current.Storage.Locations.FirstOrDefault(l => l.Enabled)?.Path
                ?? @"%USERPROFILE%\Videos\Screen Recordings";
            dir = Environment.ExpandEnvironmentVariables(primary);
        }

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Process.Start("explorer.exe", $"\"{dir}\"");
    }

    private async Task RunAudioTestAsync()
    {
        _isTestingAudio = true;
        _btnTestAudio.Enabled = false;
        _btnTestAudio.Text = "Recording 5s…";

        try
        {
            var memoryStream = new MemoryStream();
            using var capture = new WasapiCapture();
            var maxPeakSeen = -90f;
            var isFloat = capture.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat ||
                          capture.WaveFormat.BitsPerSample == 32;

            capture.DataAvailable += (_, args) =>
            {
                memoryStream.Write(args.Buffer, 0, args.BytesRecorded);
                if (isFloat)
                {
                    for (var i = 0; i <= args.BytesRecorded - 4; i += 4)
                    {
                        var sample = BitConverter.ToSingle(args.Buffer, i);
                        var abs = MathF.Abs(sample);
                        if (abs > 0.0001f)
                        {
                            var db = 20f * MathF.Log10(abs);
                            if (db > maxPeakSeen) maxPeakSeen = db;
                        }
                    }
                }
                else
                {
                    for (var i = 0; i <= args.BytesRecorded - 2; i += 2)
                    {
                        var sample = BitConverter.ToInt16(args.Buffer, i) / 32768f;
                        var abs = MathF.Abs(sample);
                        if (abs > 0.0001f)
                        {
                            var db = 20f * MathF.Log10(abs);
                            if (db > maxPeakSeen) maxPeakSeen = db;
                        }
                    }
                }
            };

            capture.StartRecording();
            await Task.Delay(5000);
            capture.StopRecording();

            _btnTestAudio.Text = "Playing…";
            memoryStream.Position = 0;
            if (memoryStream.Length > 0)
            {
                using var rawSource = new RawSourceWaveStream(memoryStream, capture.WaveFormat);
                using var player = new WasapiOut();
                player.Init(rawSource);
                player.Play();
                while (player.PlaybackState == PlaybackState.Playing)
                {
                    await Task.Delay(200);
                }
            }

            if (maxPeakSeen < -50f)
            {
                MessageBox.Show(this,
                    "Microphone peak never exceeded -50 dB.\n\nPlease check your physical mic mute switch, Windows input device settings, or microphone privacy permissions (ms-settings:privacy-microphone).",
                    "Mic Signal Low / Silent",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(this, "Audio test completed successfully. Microphone signal verified.", "Test Passed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Audio test failed: {ex.Message}", "Test Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _isTestingAudio = false;
            _btnTestAudio.Text = "Test Audio";
            _btnTestAudio.Enabled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer10Hz.Dispose();
            _timer1Hz.Dispose();
            _playMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
