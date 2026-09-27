using System.Diagnostics;
using System.Globalization;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenVault.App.Platform;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Serilog;

namespace ScreenVault.App.UI;

/// <summary>
/// Compact flyout anchored above the tray: live state and timer, the primary Start/Stop action,
/// the file being written, audio meters, storage health and quick links.
/// </summary>
public sealed class StatusForm : ModernForm
{
    private readonly IRecordingController _controller;
    private readonly IAudioEngine _audioEngine;
    private readonly IStorageManager? _storageManager;
    private readonly IMarkerService? _markerService;
    private readonly ISettingsService _settingsService;
    private readonly PlayerLauncher _playerLauncher;
    private readonly Action _openSettingsAction;
    private readonly Action? _openLibraryAction;

    private readonly System.Windows.Forms.Timer _timer10Hz;
    private readonly System.Windows.Forms.Timer _timer1Hz;
    private readonly ToolTip _toolTip = ModernToolTip.Create();

    private readonly SurfacePanel _header;
    private readonly SurfacePanel _content;
    private readonly SurfacePanel _footer;
    private readonly ModernButton _btnPin;

    private readonly SurfacePanel _statusBlock;
    private readonly StatusPill _pill;
    private readonly TextLabel _lblPart;
    private readonly TextLabel _lblElapsed;
    private readonly TextLabel _lblStats;
    private readonly CardPanel _warningBanner;
    private readonly TextLabel _lblDegraded;

    private readonly SurfacePanel _actionsRow;
    private readonly ModernButton _btnMain;
    private readonly ModernButton _btnPauseResume;
    private readonly ModernButton _btnAddMarker;

    private readonly CardPanel _fileCard;
    private readonly TextLabel _lblFile;
    private readonly TextLabel _lblFileMeta;
    private readonly ModernButton _btnPlay;
    private readonly ModernButton _btnPlayMenu;
    private readonly ContextMenuStrip _playMenu;

    private readonly CardPanel _audioCard;
    private readonly TextLabel _lblMicName;
    private readonly GlyphIcon _icoMic;
    private readonly VuMeterControl _vuMic;
    private readonly TextLabel _lblOutName;
    private readonly GlyphIcon _icoOut;
    private readonly VuMeterControl _vuOut;
    private readonly TextLabel _lblAudioSwitch;
    private readonly ModernButton _btnTestAudio;

    private readonly CardPanel _storageCard;
    private readonly StorageMeterList _storageList;

    private readonly Dictionary<string, long> _driveTotals = new(StringComparer.OrdinalIgnoreCase);

    private bool _isPinned;
    private bool _isTestingAudio;
    private bool _isStartupComplete;
    private bool _showWarnings;
    private float _pulsePhase;
    private string? _lastSavedFile;
    private DateTime _lastSavedCheckUtc = DateTime.MinValue;
    private RecorderState _previousState = RecorderState.Idle;

    public StatusForm(
        IRecordingController controller,
        IAudioEngine audioEngine,
        IStorageManager? storageManager,
        IMarkerService? markerService,
        ISettingsService settingsService,
        Action openSettingsAction,
        PlayerLauncher? playerLauncher = null,
        Action? openLibraryAction = null)
        : base(WindowChrome.Borderless)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        _storageManager = storageManager;
        _markerService = markerService;
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _openSettingsAction = openSettingsAction ?? throw new ArgumentNullException(nameof(openSettingsAction));
        _openLibraryAction = openLibraryAction;
        _playerLauncher = playerLauncher ?? new PlayerLauncher(_settingsService);

        Text = "ScreenVault Status";
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(400, 640);
        TopMost = true;
        KeyPreview = true;

        // ── Header: brand, pin, close ────────────────────────────────────────────────
        _header = new SurfacePanel { Size = new Size(400, 48), Dock = DockStyle.Top };
        var appIcon = AppIcon.Get();
        if (appIcon != null)
        {
            using var sizedIcon = new Icon(appIcon, 32, 32);
            var logo = new PictureBox
            {
                Image = sizedIcon.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds = new Rectangle(16, 15, 18, 18)
            };
            _header.Controls.Add(logo);
            EnableDrag(logo);
        }

        var title = new TextLabel("ScreenVault", Typography.BodyStrong) { Location = new Point(42, 15) };
        _header.Controls.Add(title);
        EnableDrag(title);
        EnableDrag(_header);

        _btnPin = new ModernButton(string.Empty, ButtonKind.Subtle, Glyphs.Pin)
        {
            Bounds = new Rectangle(316, 8, 32, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AccessibleName = "Keep window open"
        };
        _btnPin.Click += (_, _) => SetPinned(!_isPinned);
        _toolTip.SetToolTip(_btnPin, "Keep this window open");

        var btnClose = new ModernButton(string.Empty, ButtonKind.Subtle, Glyphs.Close)
        {
            Bounds = new Rectangle(352, 8, 32, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AccessibleName = "Close"
        };
        btnClose.Click += (_, _) => Hide();
        _toolTip.SetToolTip(btnClose, "Close (Esc)");
        _header.Controls.Add(_btnPin);
        _header.Controls.Add(btnClose);

        // ── Status block: state pill, timer, encoder stats ──────────────────────────
        _statusBlock = new SurfacePanel { Size = new Size(368, 96) };
        _pill = new StatusPill { Text = "Preparing…", Tone = Tone.Neutral, Location = new Point(0, 6) };
        _lblPart = new TextLabel(string.Empty, Typography.Caption, TextTone.Secondary)
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Bounds = new Rectangle(168, 6, 200, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _lblElapsed = new TextLabel("00:00:00", Typography.Timer) { Location = new Point(-4, 30) };
        _lblStats = new TextLabel(string.Empty, Typography.Caption, TextTone.Tertiary)
        {
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(0, 74, 368, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _statusBlock.Controls.AddRange([_pill, _lblPart, _lblElapsed, _lblStats]);

        _warningBanner = new CardPanel { AccentTone = Tone.Warning, Padding = new Padding(12, 8, 12, 8), Visible = false, Size = new Size(368, 36) };
        _lblDegraded = new TextLabel(string.Empty, Typography.Caption, TextTone.Warning, wrap: true);
        _warningBanner.Controls.Add(_lblDegraded);

        // ── Primary actions ──────────────────────────────────────────────────────────
        _actionsRow = new SurfacePanel { Size = new Size(368, 40) };
        _btnMain = new ModernButton("Preparing…", ButtonKind.Record, Glyphs.Record)
        {
            Bounds = new Rectangle(0, 0, 272, 40),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Enabled = false
        };
        _btnMain.Click += async (_, _) => await OnMainButtonClickedAsync();

        _btnPauseResume = new ModernButton(string.Empty, ButtonKind.Secondary, Glyphs.Pause)
        {
            Bounds = new Rectangle(280, 0, 40, 40),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AccessibleName = "Pause recording",
            Enabled = false
        };
        _btnPauseResume.Click += async (_, _) => await TogglePauseAsync();

        _btnAddMarker = new ModernButton(string.Empty, ButtonKind.Secondary, Glyphs.Flag)
        {
            Bounds = new Rectangle(328, 0, 40, 40),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AccessibleName = "Add marker"
        };
        _btnAddMarker.Click += (_, _) => ShowMarkerDialog();
        _actionsRow.Controls.AddRange([_btnMain, _btnPauseResume, _btnAddMarker]);

        // ── Current file ─────────────────────────────────────────────────────────────
        _fileCard = new CardPanel { ManualLayout = true, Size = new Size(368, 68) };
        var fileBadge = new GlyphBadge { Glyph = Glyphs.Video, Tone = Tone.Accent, Bounds = new Rectangle(14, 16, 36, 36) };
        _lblFile = new TextLabel("No recording yet", Typography.BodyStrong)
        {
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(60, 14, 188, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Cursor = Cursors.Hand
        };
        _lblFile.Click += (_, _) => RevealCurrentFile();
        _lblFileMeta = new TextLabel("Recordings appear here while you record", Typography.Caption, TextTone.Secondary)
        {
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(60, 35, 188, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _btnPlay = new ModernButton("Play", ButtonKind.Secondary, Glyphs.Play)
        {
            Bounds = new Rectangle(254, 18, 72, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
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

        _btnPlayMenu = new ModernButton(string.Empty, ButtonKind.Subtle, Glyphs.ChevronDown)
        {
            Bounds = new Rectangle(328, 18, 28, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AccessibleName = "More playback options"
        };
        _playMenu = new ContextMenuStrip();
        ModernMenu.Apply(_playMenu);
        _playMenu.Items.Add(ModernMenu.Item("Play current part", Glyphs.Play, (_, _) => PlayCurrentFile()));
        _playMenu.Items.Add(ModernMenu.Item("Play last saved recording", Glyphs.Video, (_, _) => PlayLastSavedFile()));
        _playMenu.Items.Add(new ToolStripSeparator());
        _playMenu.Items.Add(ModernMenu.Item("Open with…", Glyphs.OpenWith, (_, _) => OpenWithDialog()));
        _btnPlayMenu.Click += (_, _) => _playMenu.Show(_btnPlayMenu, new Point(0, _btnPlayMenu.Height));
        _toolTip.SetToolTip(_btnPlayMenu, "More playback options");
        _fileCard.Controls.AddRange([fileBadge, _lblFile, _lblFileMeta, _btnPlay, _btnPlayMenu]);

        // ── Audio ────────────────────────────────────────────────────────────────────
        _audioCard = new CardPanel { ManualLayout = true, Size = new Size(368, 132) };
        var audioTitle = new TextLabel("Audio", Typography.Subtitle) { Location = new Point(14, 12) };
        _btnTestAudio = new ModernButton("Test audio", ButtonKind.Subtle, Glyphs.Microphone)
        {
            Bounds = new Rectangle(248, 8, 108, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _btnTestAudio.Click += async (_, _) => await RunAudioTestAsync();
        _toolTip.SetToolTip(_btnTestAudio, "Records 5 seconds from your microphone and plays it back (only while not recording)");

        _icoMic = new GlyphIcon { Glyph = Glyphs.Microphone, Bounds = new Rectangle(14, 46, 20, 20) };
        _lblMicName = new TextLabel("Microphone", Typography.Body) { AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(40, 46, 128, 20) };
        _vuMic = new VuMeterControl { Bounds = new Rectangle(176, 46, 180, 20), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Microphone level" };

        _icoOut = new GlyphIcon { Glyph = Glyphs.Volume, Bounds = new Rectangle(14, 76, 20, 20) };
        _lblOutName = new TextLabel("System audio", Typography.Body) { AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(40, 76, 128, 20) };
        _vuOut = new VuMeterControl { Bounds = new Rectangle(176, 76, 180, 20), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "System audio level" };

        _lblAudioSwitch = new TextLabel("No device changes yet", Typography.Caption, TextTone.Tertiary)
        {
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(14, 104, 342, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _audioCard.Controls.AddRange([audioTitle, _btnTestAudio, _icoMic, _lblMicName, _vuMic, _icoOut, _lblOutName, _vuOut, _lblAudioSwitch]);

        // ── Storage ──────────────────────────────────────────────────────────────────
        _storageCard = new CardPanel { ManualLayout = true, Size = new Size(368, 104) };
        var storageTitle = new TextLabel("Storage", Typography.Subtitle) { Location = new Point(14, 12) };
        _storageList = new StorageMeterList { Bounds = new Rectangle(14, 42, 340, 50), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _storageCard.Controls.AddRange([storageTitle, _storageList]);

        // ── Scrollable content ───────────────────────────────────────────────────────
        _content = new SurfacePanel { Size = new Size(400, 536), Dock = DockStyle.Fill, AutoScroll = true };
        _content.Controls.AddRange([_statusBlock, _warningBanner, _actionsRow, _fileCard, _audioCard, _storageCard]);
        _content.Resize += (_, _) => LayoutContent();

        // ── Footer: quick links ──────────────────────────────────────────────────────
        _footer = new SurfacePanel { Size = new Size(400, 56), Dock = DockStyle.Bottom, TopDivider = true };
        var btnLibrary = new ModernButton("Recordings", ButtonKind.Subtle, Glyphs.Library) { Bounds = new Rectangle(12, 10, 120, 36) };
        btnLibrary.Click += (_, _) => _openLibraryAction?.Invoke();
        btnLibrary.Enabled = _openLibraryAction != null;
        var btnFolder = new ModernButton("Folder", ButtonKind.Subtle, Glyphs.FolderOpen) { Bounds = new Rectangle(140, 10, 120, 36) };
        btnFolder.Click += (_, _) => OpenRecordingsFolder();
        var btnSettings = new ModernButton("Settings", ButtonKind.Subtle, Glyphs.Settings) { Bounds = new Rectangle(268, 10, 120, 36) };
        btnSettings.Click += (_, _) => _openSettingsAction();
        _toolTip.SetToolTip(btnLibrary, "Browse, play and export past recordings");
        _toolTip.SetToolTip(btnFolder, "Open the folder that contains the current recording");
        _toolTip.SetToolTip(btnSettings, "Settings");
        _footer.Controls.AddRange([btnLibrary, btnFolder, btnSettings]);

        Controls.Add(_content);
        Controls.Add(_header);
        Controls.Add(_footer);

        // Timers: 10 Hz (meters, pulse) and 1 Hz (everything else). Both stop while hidden.
        _timer10Hz = new System.Windows.Forms.Timer { Interval = 100 };
        _timer10Hz.Tick += OnTick10Hz;
        _timer1Hz = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer1Hz.Tick += (_, _) => Refresh1Hz();

        Deactivate += (_, _) =>
        {
            // Stay open while one of our own dialogs (stop confirmation, marker note…) is showing.
            if (!_isPinned && OwnedForms.Length == 0)
            {
                Hide();
            }
        };

        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                _audioEngine.SetMonitoring(this, true);
                UpdateToolTips();
                Refresh1Hz();
                _timer10Hz.Start();
                _timer1Hz.Start();
            }
            else
            {
                _audioEngine.SetMonitoring(this, false);
                _timer10Hz.Stop();
                _timer1Hz.Stop();
            }
        };

        ResumeLayout(false);
        PerformLayout();
    }

    public void SetStartupComplete(bool complete = true)
    {
        _isStartupComplete = complete;
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(Refresh1Hz);
        }
    }

    public void AnchorNearTray()
    {
        Refresh1Hz();
        FitToContent(keepBottomEdge: false);
        var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        var margin = LogicalToDeviceUnits(12);
        Left = screen.Right - Width - margin;
        Top = screen.Bottom - Height - margin;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToContent(keepBottomEdge: true);
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
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void SetPinned(bool pinned)
    {
        _isPinned = pinned;
        TopMost = pinned;
        _btnPin.Glyph = pinned ? Glyphs.Pinned : Glyphs.Pin;
        _btnPin.Kind = pinned ? ButtonKind.Secondary : ButtonKind.Subtle;
        _btnPin.AccessibleName = pinned ? "Unpin window" : "Keep window open";
        _toolTip.SetToolTip(_btnPin, pinned ? "Pinned — click to close automatically again" : "Keep this window open");
    }

    private void UpdateToolTips()
    {
        var hotkeys = _settingsService.Current.Hotkeys;
        _toolTip.SetToolTip(_btnMain, $"Start recording, or stop and save ({HotkeyField.DisplayText(hotkeys.StartStop)})");
        _toolTip.SetToolTip(_btnPauseResume, $"Pause or resume ({HotkeyField.DisplayText(hotkeys.PauseResume)})");
        _toolTip.SetToolTip(_btnAddMarker, $"Add a marker to find this moment later ({HotkeyField.DisplayText(hotkeys.AddMarker)})");
        _toolTip.SetToolTip(_btnPlay, "Play the current part, or the last saved recording");
        _toolTip.SetToolTip(_lblFile, "Click to show the file in Explorer");
    }

    /// <summary>Stacks the content blocks and returns the content height (device pixels).</summary>
    private int LayoutContent()
    {
        int S(int value) => LogicalToDeviceUnits(value);

        var width = _content.ClientSize.Width - S(32);
        if (width <= 0)
        {
            return 0;
        }

        var x = S(16);
        var y = S(4) + _content.AutoScrollPosition.Y;
        var spacing = S(10);

        void Place(Control control, int height)
        {
            control.SetBounds(x, y, width, height);
            y += height + spacing;
        }

        Place(_statusBlock, _statusBlock.Height);
        if (_showWarnings)
        {
            var textHeight = TextLabel.MeasureHeight(_lblDegraded.Text, _lblDegraded.Font, width - _warningBanner.Padding.Horizontal);
            _warningBanner.Visible = true;
            Place(_warningBanner, textHeight + _warningBanner.Padding.Vertical);
        }
        else
        {
            _warningBanner.Visible = false;
        }

        Place(_actionsRow, _actionsRow.Height);
        Place(_fileCard, _fileCard.Height);
        Place(_audioCard, _audioCard.Height);
        _storageCard.Height = _storageList.Top + _storageList.PreferredHeight + S(10);
        Place(_storageCard, _storageCard.Height);

        var total = y - spacing + S(12) - _content.AutoScrollPosition.Y;
        var min = new Size(0, total);
        if (_content.AutoScrollMinSize != min)
        {
            _content.AutoScrollMinSize = min;
        }

        return total;
    }

    private void FitToContent(bool keepBottomEdge)
    {
        var contentHeight = LayoutContent();
        var screen = Screen.FromControl(this).WorkingArea;
        var desired = Math.Min(_header.Height + contentHeight + _footer.Height, screen.Height - LogicalToDeviceUnits(24));
        if (ClientSize.Height == desired)
        {
            return;
        }

        var bottom = Bottom;
        ClientSize = new Size(ClientSize.Width, desired);
        if (keepBottomEdge && Visible)
        {
            Top = Math.Max(screen.Top, bottom - Height);
        }
    }

    private void OnTick10Hz(object? sender, EventArgs e)
    {
        var audioStatus = _audioEngine.GetStatus();
        float micPeak = -60f, micRms = -60f, sysPeak = -60f, sysRms = -60f;
        foreach (var device in audioStatus.ActiveDevices)
        {
            if (device.IsLoopback)
            {
                sysPeak = Math.Max(sysPeak, device.PeakDb);
                sysRms = Math.Max(sysRms, device.RmsDb);
            }
            else
            {
                micPeak = Math.Max(micPeak, device.PeakDb);
                micRms = Math.Max(micRms, device.RmsDb);
            }
        }

        _vuMic.IsMuted = audioStatus.IsMicMuted;
        _vuMic.SetLevels(micPeak, micPeak, micRms, micRms);
        _vuOut.SetLevels(sysPeak, sysPeak, sysRms, sysRms);

        // Gentle "live" pulse on the Recording pill.
        if (_controller.State == RecorderState.Recording)
        {
            _pulsePhase = (_pulsePhase + 0.21f) % (MathF.PI * 2f);
            _pill.DotOpacity = 0.5f + (0.5f * MathF.Cos(_pulsePhase));
        }
        else if (_pill.DotOpacity < 1f)
        {
            _pill.DotOpacity = 1f;
        }
    }

    private void Refresh1Hz()
    {
        var health = _controller.Health;
        var elapsed = health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        _lblElapsed.Text = elapsed;

        _lblPart.Text = health.PartElapsed > TimeSpan.Zero || health.State is RecorderState.Recording or RecorderState.Paused
            ? $"Part {health.PartIndex} · {health.PartElapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture)}"
            : string.Empty;

        ApplyStateVisuals(health);

        // Warnings banner (degraded reasons)
        var warnings = health.DegradedWarnings.Count > 0 ? string.Join(Environment.NewLine, health.DegradedWarnings.Select(w => "• " + w)) : string.Empty;
        var showWarnings = warnings.Length > 0;
        var layoutChanged = showWarnings != _showWarnings || (showWarnings && _lblDegraded.Text != warnings);
        _showWarnings = showWarnings;
        _lblDegraded.Text = warnings;

        // Current file
        if (_previousState != health.State)
        {
            _lastSavedCheckUtc = DateTime.MinValue; // re-scan after a state change (e.g. a recording was just saved)
            _previousState = health.State;
        }

        var hasSavedFile = GetLastSavedFilePath() != null;
        if (!string.IsNullOrEmpty(health.CurrentFilePath))
        {
            var mb = health.CurrentFileBytes / (1024.0 * 1024.0);
            _lblFile.Text = Path.GetFileName(health.CurrentFilePath);
            _lblFileMeta.Text = string.Create(CultureInfo.CurrentCulture, $"{mb:F1} MB · Part {health.PartIndex} · click the name to show in folder");
            _btnPlay.Enabled = File.Exists(health.CurrentFilePath) || hasSavedFile;
        }
        else
        {
            _lblFile.Text = hasSavedFile ? "Not recording" : "No recording yet";
            _lblFileMeta.Text = hasSavedFile ? "Play opens your most recent recording" : "Recordings appear here while you record";
            _btnPlay.Enabled = hasSavedFile;
        }

        // Audio devices
        var audioStatus = _audioEngine.GetStatus();
        ApplyDeviceStatus(_lblMicName, _icoMic, audioStatus.MicDisplayStatus, Glyphs.Microphone, "Microphone");
        ApplyDeviceStatus(_lblOutName, _icoOut, audioStatus.SystemDisplayStatus, Glyphs.Volume, "System audio");
        if (audioStatus.IsMicMuted)
        {
            _icoMic.Tone = TextTone.Warning;
        }

        _lblAudioSwitch.Text = $"Last switch: {audioStatus.LastSwitchSummary}";
        _toolTip.SetToolTip(_lblAudioSwitch, audioStatus.LastSwitchSummary);

        // Storage
        var storageRowsBefore = _storageList.PreferredHeight;
        if (_storageManager != null)
        {
            _storageList.SetRows(BuildStorageRows(_storageManager.GetStatus()));
        }

        layoutChanged |= storageRowsBefore != _storageList.PreferredHeight;

        // Encoder stats
        var targetFps = _settingsService.Current.Video.FrameRate;
        var actualFps = health.ActualFps > 0 ? health.ActualFps : targetFps;
        var slow = health.Speed > 0 && health.Speed < 0.97;
        _lblStats.Text = health.State is RecorderState.Recording or RecorderState.Paused
            ? string.Create(CultureInfo.CurrentCulture, $"{health.EncoderProfile} · {actualFps:F0} of {targetFps} fps · {health.Speed:F2}× speed{(slow ? " — encoder is falling behind" : string.Empty)}")
            : string.Create(CultureInfo.CurrentCulture, $"{targetFps} fps · {_settingsService.Current.Video.Quality} quality · {_settingsService.Current.Storage.OutputFormat.ToString().ToUpperInvariant()} files");
        _lblStats.Tone = slow ? TextTone.Warning : TextTone.Tertiary;

        if (layoutChanged)
        {
            FitToContent(keepBottomEdge: true);
        }
    }

    private void ApplyStateVisuals(HealthSnapshot health)
    {
        switch (health.State)
        {
            case RecorderState.Recording:
                SetPill("Recording", Tone.Danger);
                SetMainButton("Stop & save", ButtonKind.Strong, Glyphs.Stop, enabled: true);
                SetPauseButton(paused: false, enabled: true);
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Paused:
                SetPill("Paused", Tone.Warning);
                SetMainButton("Stop & save", ButtonKind.Strong, Glyphs.Stop, enabled: true);
                SetPauseButton(paused: true, enabled: true);
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Faulted when health.Desired != DesiredState.Stopped:
                // ScreenVault keeps retrying on its own; the user must still be able to give up.
                SetPill(health.Desired == DesiredState.Paused ? "Paused" : "Not recording — retrying", health.Desired == DesiredState.Paused ? Tone.Warning : Tone.Danger);
                SetMainButton("Stop", ButtonKind.Strong, Glyphs.Stop, enabled: true);
                SetPauseButton(paused: health.Desired == DesiredState.Paused, enabled: health.Desired == DesiredState.Paused);
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Starting:
                SetPill("Starting…", Tone.Accent);
                SetMainButton("Stop", ButtonKind.Strong, Glyphs.Stop, enabled: true);
                SetPauseButton(paused: false, enabled: false);
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Saving:
            case RecorderState.Stopping:
                SetPill("Saving…", Tone.Neutral);
                SetMainButton("Saving…", ButtonKind.Strong, Glyphs.Save, enabled: false);
                SetPauseButton(paused: false, enabled: false);
                _btnTestAudio.Enabled = false;
                break;

            case RecorderState.Recovering:
                SetPill("Recovering…", Tone.Warning);
                SetMainButton("Stop", ButtonKind.Strong, Glyphs.Stop, enabled: true);
                SetPauseButton(paused: false, enabled: false);
                _btnTestAudio.Enabled = false;
                break;

            default:
                if (_isStartupComplete)
                {
                    SetPill("Ready", Tone.Success);
                    SetMainButton("Start recording", ButtonKind.Record, Glyphs.Record, enabled: true);
                }
                else
                {
                    SetPill("Preparing…", Tone.Neutral);
                    SetMainButton("Preparing…", ButtonKind.Record, Glyphs.Record, enabled: false);
                }

                SetPauseButton(paused: false, enabled: false);
                _btnTestAudio.Enabled = !_isTestingAudio && _isStartupComplete;
                break;
        }

        _btnAddMarker.Enabled = health.State is RecorderState.Recording or RecorderState.Paused;
    }

    private void SetPill(string text, Tone tone)
    {
        _pill.Text = text;
        _pill.Tone = tone;
    }

    private void SetMainButton(string text, ButtonKind kind, char glyph, bool enabled)
    {
        _btnMain.Text = text;
        _btnMain.Kind = kind;
        _btnMain.Glyph = glyph;
        _btnMain.GlyphColor = kind == ButtonKind.Strong && glyph == Glyphs.Stop ? Theme.Current.Danger : Color.Empty;
        _btnMain.Enabled = enabled;
    }

    private void SetPauseButton(bool paused, bool enabled)
    {
        _btnPauseResume.Glyph = paused ? Glyphs.Play : Glyphs.Pause;
        _btnPauseResume.AccessibleName = paused ? "Resume recording" : "Pause recording";
        _btnPauseResume.Enabled = enabled;
    }

    private void ApplyDeviceStatus(TextLabel label, GlyphIcon icon, string status, char glyph, string role)
    {
        // Core reports "✔ Name", "✖ Reason" or "⟳ Retrying: reason"; show the text with a matching tone instead of symbols.
        var text = status;
        var tone = TextTone.Primary;
        var iconTone = TextTone.Secondary;
        if (status.StartsWith('✔'))
        {
            text = status[1..].Trim();
        }
        else if (status.StartsWith('✖'))
        {
            text = status[1..].Trim();
            tone = TextTone.Tertiary;
            iconTone = TextTone.Tertiary;
        }
        else if (status.StartsWith('⟳'))
        {
            text = status[1..].Trim();
            tone = TextTone.Warning;
            iconTone = TextTone.Warning;
        }

        label.Text = text;
        label.Tone = tone;
        icon.Glyph = glyph;
        icon.Tone = iconTone;
        _toolTip.SetToolTip(label, $"{role}: {text}");
    }

    private List<StorageMeterRow> BuildStorageRows(StorageStatus status)
    {
        var rows = new List<StorageMeterRow>(status.Locations.Count);
        foreach (var location in status.Locations)
        {
            var isActive = string.Equals(location.ExpandedPath, status.ActiveLocationPath, StringComparison.OrdinalIgnoreCase);
            var expanded = location.ExpandedPath;
            var (stateText, tone) = location.State switch
            {
                StorageLocationState.Low => ("Low on space", Tone.Warning),
                StorageLocationState.Failed => ("Unavailable — retrying", Tone.Danger),
                StorageLocationState.Disabled => ("Disabled", Tone.Neutral),
                _ => ("Healthy", Tone.Success)
            };

            if (isActive)
            {
                stateText = status.IsInEmergencyMode ? "Saving here · emergency mode" : $"Saving here · {stateText}";
                if (status.IsInEmergencyMode)
                {
                    tone = Tone.Danger;
                }
            }

            rows.Add(new StorageMeterRow(expanded, location.AvailableFreeBytes, GetDriveTotal(expanded), stateText, tone, isActive));
        }

        return rows;
    }

    private long GetDriveTotal(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return 0;
            }

            if (_driveTotals.TryGetValue(root, out var cached))
            {
                return cached;
            }

            var drive = new DriveInfo(root);
            var total = drive.IsReady ? drive.TotalSize : 0;
            if (total > 0)
            {
                _driveTotals[root] = total;
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Could not query drive size for {Path}", path);
            return 0;
        }
    }

    private async Task OnMainButtonClickedAsync()
    {
        _btnMain.Enabled = false;
        try
        {
            if (_controller.Desired != DesiredState.Stopped)
            {
                // Only ask when something is actually being recorded.
                if (_controller.State is RecorderState.Recording or RecorderState.Paused &&
                    !RecordingPrompts.ConfirmStop(this, _settingsService))
                {
                    return;
                }

                await _controller.StopAsync().ConfigureAwait(true);
            }
            else if (_controller.State is RecorderState.Idle or RecorderState.Faulted)
            {
                await _controller.StartAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Start/stop from the status window failed.");
        }
        finally
        {
            if (!IsDisposed)
            {
                Refresh1Hz();
            }
        }
    }

    private async Task TogglePauseAsync()
    {
        _btnPauseResume.Enabled = false;
        try
        {
            if (_controller.Desired == DesiredState.Recording)
            {
                await _controller.PauseAsync().ConfigureAwait(true);
            }
            else if (_controller.Desired == DesiredState.Paused)
            {
                await _controller.ResumeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Pause/resume from the status window failed.");
        }
        finally
        {
            if (!IsDisposed)
            {
                Refresh1Hz();
            }
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
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = GetLastSavedFilePath();
        }

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            StartExplorer($"/select,\"{path}\"");
        }
    }

    private static void StartExplorer(string arguments)
    {
        try
        {
            Process.Start("explorer.exe", arguments);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "Could not open Explorer.");
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
            ModernDialog.Info(this, "Nothing to play yet", "The current part is not on disk yet. Try again in a few seconds.");
        }
    }

    private void PlayLastSavedFile()
    {
        var latestFile = GetLastSavedFilePath(forceRefresh: true);
        if (!string.IsNullOrEmpty(latestFile) && File.Exists(latestFile))
        {
            _playerLauncher.Launch(latestFile);
            return;
        }

        ModernDialog.Info(this, "No saved recordings yet", "Recordings will appear here after you record and stop.");
    }

    /// <summary>Most recent video in the primary location. Cached: scanning large folders every second is expensive.</summary>
    private string? GetLastSavedFilePath(bool forceRefresh = false)
    {
        if (!forceRefresh && DateTime.UtcNow - _lastSavedCheckUtc < TimeSpan.FromSeconds(15))
        {
            return _lastSavedFile;
        }

        _lastSavedCheckUtc = DateTime.UtcNow;
        try
        {
            var recordingsDir = _settingsService.Current.Storage.Locations.FirstOrDefault(l => l.Enabled)?.Path
                ?? @"%USERPROFILE%\Videos\Screen Recordings";
            var expandedDir = Environment.ExpandEnvironmentVariables(recordingsDir);

            _lastSavedFile = Directory.Exists(expandedDir)
                ? Directory.EnumerateFiles(expandedDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Debug(ex, "Could not scan for the last saved recording.");
            _lastSavedFile = null;
        }

        return _lastSavedFile;
    }

    private void OpenWithDialog()
    {
        var path = _controller.Health.CurrentFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = GetLastSavedFilePath(forceRefresh: true);
        }

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            PlayerLauncher.OpenWithDialog(path);
        }
        else
        {
            ModernDialog.Info(this, "No video to open", "There is no playable recording yet.");
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

        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ModernDialog.Warning(this, "Can't open the recordings folder", $"{dir}\n\n{ex.Message}");
            return;
        }

        StartExplorer($"\"{dir}\"");
    }

    private async Task RunAudioTestAsync()
    {
        _isTestingAudio = true;
        _btnTestAudio.Enabled = false;
        _btnTestAudio.Text = "Listening… 5s";

        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        try
        {
            // Test the microphone that recordings actually use (the settings pick communications or
            // multimedia default, which is often not the device WasapiCapture would pick by itself).
            var audioSettings = _settingsService.Current.Audio;
            if (audioSettings.MicMode == MicMode.None)
            {
                ModernDialog.Info(this, "The microphone is turned off", "Recording the microphone is disabled in Settings → Audio.");
                return;
            }

            enumerator = new MMDeviceEnumerator();
            var endpoint = EndpointResolver.Resolve(enumerator, audioSettings).DesiredEndpoints.FirstOrDefault(e => !e.IsLoopback);
            if (endpoint == null)
            {
                ModernDialog.Warning(this, "No microphone found", "Connect a microphone or headset, then check the input device in Windows Sound settings.");
                return;
            }

            device = enumerator.GetDevice(endpoint.Id);
            using var memoryStream = new MemoryStream();
            using var capture = new WasapiCapture(device);
            var maxPeakSeen = -90f;
            var isFloat = capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                          capture.WaveFormat.BitsPerSample == 32;
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.RecordingStopped += (_, _) => stopped.TrySetResult();

            capture.DataAvailable += (_, args) =>
            {
                memoryStream.Write(args.Buffer, 0, args.BytesRecorded);
                var step = isFloat ? 4 : 2;
                for (var i = 0; i <= args.BytesRecorded - step; i += step)
                {
                    var sample = isFloat ? BitConverter.ToSingle(args.Buffer, i) : BitConverter.ToInt16(args.Buffer, i) / 32768f;
                    var abs = MathF.Abs(sample);
                    if (abs > 0.0001f)
                    {
                        var db = 20f * MathF.Log10(abs);
                        if (db > maxPeakSeen)
                        {
                            maxPeakSeen = db;
                        }
                    }
                }
            };

            capture.StartRecording();
            await Task.Delay(5000);
            capture.StopRecording();

            // Capture stops asynchronously: wait until the last buffer has been written.
            await Task.WhenAny(stopped.Task, Task.Delay(2000));

            _btnTestAudio.Text = "Playing back…";
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

            if (IsDisposed)
            {
                return;
            }

            if (maxPeakSeen < -50f)
            {
                ModernDialog.Warning(this,
                    "Your microphone seems silent",
                    $"The level of \"{endpoint.Name}\" never rose above −50 dB. Check the mute switch on your headset, the input device in Windows Sound settings, and Settings → Privacy → Microphone.");
            }
            else
            {
                ModernDialog.Success(this, "Microphone works", $"ScreenVault heard you clearly on \"{endpoint.Name}\". You're ready to record.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Audio test failed.");
            if (!IsDisposed)
            {
                ModernDialog.Error(this, "Audio test failed", ex.Message);
            }
        }
        finally
        {
            device?.Dispose();
            enumerator?.Dispose();
            _isTestingAudio = false;
            if (!IsDisposed)
            {
                _btnTestAudio.Text = "Test audio";
                _btnTestAudio.Enabled = _controller.State is RecorderState.Idle;
            }
        }
    }
}
