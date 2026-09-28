using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using ScreenVault.App.Platform;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

/// <summary>Windows 11 style settings: sidebar navigation, grouped setting cards, Save/Apply footer.</summary>
public sealed class SettingsForm : ModernForm
{
    private readonly ISettingsService _settingsService;
    private readonly IAudioEngine? _audioEngine;
    private readonly ToolTip _toolTip = ModernToolTip.Create();
    private readonly System.Windows.Forms.Timer _meterTimer;
    private readonly NavigationList _nav;
    private readonly List<StackPanel> _pages = [];
    private readonly Dictionary<string, long> _freeSpaceCache = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _workingCopy;
    private int _audioPageIndex;

    // Values behind the drop-downs. A value the list doesn't offer (e.g. a frame rate lowered
    // automatically) is added as an extra entry so saving never silently changes it.
    private readonly List<int> _frameRateValues = [15, 24, 30];
    private readonly List<int> _splitMinuteValues = [5, 10, 15, 30, 60];
    private readonly List<MicMode> _micModeValues = [MicMode.DefaultCommunications, MicMode.DefaultMultimedia, MicMode.Specific, MicMode.None];
    private readonly List<OutputMode> _outputModeValues = [OutputMode.DefaultPlusCommunications, OutputMode.Default, OutputMode.Specific, OutputMode.None];

    // Populated once from Windows: which physical monitor and, when a specific device is chosen,
    // which audio endpoint each device row's index maps to. A device unplugged since it was chosen
    // is still added as an extra entry (see SelectValue) so saving never silently changes it.
    private readonly List<int> _monitorIndexValues = [];
    private readonly List<string?> _micDeviceIds = [];
    private readonly List<string?> _outputDeviceIds = [];

    // General
    private readonly ToggleSwitch _chkStartWithWindows = new();
    private readonly ToggleSwitch _chkAutoStartRecording = new();
    private readonly ToggleSwitch _chkStartMinimized = new();
    private readonly NumberField _numStartupDelay = new() { Minimum = 0, Maximum = 120, Suffix = "s" };
    private readonly ToggleSwitch _chkConfirmStop = new();
    private readonly ToggleSwitch _chkNotifyDevice = new();
    private readonly ToggleSwitch _chkNotifyStorage = new();
    private readonly ModernComboBox _cmbTheme = new();
    private readonly ModernComboBox _cmbMeetingMode = new();
    private readonly ToggleSwitch _chkPromptStopMeeting = new();

    // Video
    private readonly ModernComboBox _cmbMonitor = new();
    private readonly ModernComboBox _cmbFrameRate = new();
    private readonly ModernComboBox _cmbQuality = new();
    private readonly ModernComboBox _cmbEncoder = new();
    private readonly ModernButton _btnRedetectEncoder = new("Detect now", ButtonKind.Secondary, Glyphs.Refresh);
    private readonly ToggleSwitch _chkCaptureCursor = new();
    private readonly ToggleSwitch _chkDownscale = new();
    private readonly ThemedListView _lstProbeResults = new();
    private readonly SettingRow _encoderRow;

    // Audio
    private readonly ModernComboBox _cmbMicMode = new();
    private readonly ModernComboBox _cmbMicDevice = new();
    private readonly ModernComboBox _cmbOutputMode = new();
    private readonly ModernComboBox _cmbOutputDevice = new();
    private SettingRow _micDeviceRow = null!;
    private SettingRow _outputDeviceRow = null!;
    private readonly ModernSlider _trkMicGain = new() { Minimum = -20, Maximum = 20, Origin = 0 };
    private readonly TextLabel _lblMicGainVal = new("0 dB", Typography.BodyStrong);
    private readonly ModernSlider _trkSysGain = new() { Minimum = -20, Maximum = 20, Origin = 0 };
    private readonly TextLabel _lblSysGainVal = new("0 dB", Typography.BodyStrong);
    private readonly VuMeterControl _vuMic = new() { AccessibleName = "Microphone level" };
    private readonly VuMeterControl _vuSys = new() { AccessibleName = "System audio level" };
    private readonly NumberField _numJitterBuffer = new() { Minimum = 30, Maximum = 500, Increment = 10, Suffix = "ms" };
    private readonly NumberField _numAvOffset = new() { Minimum = -500, Maximum = 500, Increment = 10, Suffix = "ms" };

    // Storage & saving
    private readonly ThemedListBox _lstLocations = new() { ItemHeightLogical = 56 };
    private readonly ModernButton _btnAddLocation = new("Add folder…", ButtonKind.Secondary, Glyphs.Add);
    private readonly ModernButton _btnRemoveLocation = new("Remove", ButtonKind.Subtle, Glyphs.Delete);
    private readonly ModernButton _btnMoveUp = new("Move up", ButtonKind.Subtle, Glyphs.ChevronUp);
    private readonly ModernButton _btnMoveDown = new("Move down", ButtonKind.Subtle, Glyphs.ChevronDown);
    private readonly ModernComboBox _cmbSplitMinutes = new();
    private readonly ModernComboBox _cmbOutputFormat = new();
    private readonly ToggleSwitch _chkKeepTs = new();
    private readonly ToggleSwitch _chkFailback = new();
    private readonly ToggleSwitch _chkRetention = new();
    private readonly NumberField _numRetentionDays = new() { Minimum = 1, Maximum = 365, Value = 30, Suffix = "days" };
    private readonly ToggleSwitch _chkShowSavedDialog = new();
    private readonly ToggleSwitch _chkMergeOnSave = new();
    private readonly ToggleSwitch _chkDeletePartsAfterMerge = new();

    // Hotkeys
    private readonly HotkeyField _txtHkStartStop = new() { AccessibleName = "Start or stop shortcut" };
    private readonly HotkeyField _txtHkMuteMic = new() { AccessibleName = "Mute microphone shortcut" };
    private readonly HotkeyField _txtHkMarker = new() { AccessibleName = "Add marker shortcut" };
    private readonly HotkeyField _txtHkPause = new() { AccessibleName = "Pause or resume shortcut" };
    private readonly HotkeyField _txtHkStatus = new() { AccessibleName = "Show status shortcut" };

    // Advanced
    private readonly TextField _txtFfmpegPath = new() { PlaceholderText = "Bundled FFmpeg (recommended)" };
    private readonly ModernComboBox _cmbLogLevel = new();

    // Footer
    private readonly TextLabel _lblValidation = new(string.Empty, Typography.Body, TextTone.Danger) { AutoSize = false, AutoEllipsis = true };

    public SettingsForm(ISettingsService settingsService, IAudioEngine? audioEngine = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _audioEngine = audioEngine;
        _workingCopy = CloneSettings(_settingsService.Current);

        Text = "ScreenVault Settings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 660);
        MinimumSize = new Size(780, 540);
        MaximizeBox = true;
        MinimizeBox = true;

        // ── Combo box items ──────────────────────────────────────────────────────────
        _cmbTheme.Items.AddRange(["Use Windows setting", "Light", "Dark"]);
        _cmbMeetingMode.Items.AddRange(["Ask me (recommended)", "Start recording automatically", "Do nothing"]);
        _cmbFrameRate.Items.AddRange(["15 fps (recommended)", "24 fps", "30 fps"]);
        _cmbQuality.Items.AddRange(["Small — lowest CPU and size", "Balanced (recommended)", "High — crisp small text"]);
        _cmbEncoder.Items.AddRange(["Auto", "nvenc-d3d11", "amf-d3d11", "qsv-hwmap", "nvenc-sysmem", "amf-sysmem", "qsv-sysmem", "x264"]);
        _cmbMicMode.Items.AddRange(["Windows default – communications (recommended)", "Windows default – multimedia", "A specific microphone…", "Don't record the microphone"]);
        _cmbOutputMode.Items.AddRange(["Default + communications (recommended)", "Default output only", "A specific output device…", "Don't record system audio"]);
        _cmbSplitMinutes.Items.AddRange(["5 minutes", "10 minutes (recommended)", "15 minutes", "30 minutes", "60 minutes"]);
        _cmbOutputFormat.Items.AddRange(["MKV (recommended)", "MP4 (most compatible)", "TS (raw live format)"]);
        _cmbLogLevel.Items.AddRange(["Debug", "Information", "Warning", "Error"]);
        foreach (var combo in new[] { _cmbTheme, _cmbMeetingMode, _cmbMonitor, _cmbFrameRate, _cmbQuality, _cmbEncoder, _cmbMicMode, _cmbMicDevice, _cmbOutputMode, _cmbOutputDevice, _cmbSplitMinutes, _cmbOutputFormat, _cmbLogLevel })
        {
            combo.Width = 260;
        }

        PopulateMonitorList();
        PopulateAudioDeviceLists();
        _cmbMicMode.SelectedIndexChanged += (_, _) => UpdateAudioDeviceRowVisibility();
        _cmbOutputMode.SelectedIndexChanged += (_, _) => UpdateAudioDeviceRowVisibility();

        _numStartupDelay.Width = 110;
        _numJitterBuffer.Width = 120;
        _numAvOffset.Width = 120;
        _numRetentionDays.Width = 130;
        foreach (var hotkey in new[] { _txtHkStartStop, _txtHkMuteMic, _txtHkMarker, _txtHkPause, _txtHkStatus })
        {
            hotkey.Width = 260;
        }

        // ── Pages ────────────────────────────────────────────────────────────────────
        var host = new SurfacePanel { Size = new Size(688, 588), Dock = DockStyle.Fill };

        // General
        var general = CreatePage("General", "Startup behavior, confirmations, meetings, notifications and appearance.");
        general.Controls.Add(Section("Startup"));
        general.Controls.Add(Card(
            Row("Start with Windows", "Open ScreenVault in the notification area when you sign in.", _chkStartWithWindows, Glyphs.Monitor),
            Row("Start recording automatically", "Begin recording as soon as ScreenVault starts, after the startup delay.", _chkAutoStartRecording, Glyphs.Record),
            Row("Start minimized to the tray", "Don't open the status window when ScreenVault starts.", _chkStartMinimized, Glyphs.Pin),
            Row("Startup delay", "Gives Windows audio time to get ready after you sign in.", _numStartupDelay, Glyphs.Clock)));
        general.Controls.Add(Section("Recording"));
        general.Controls.Add(Card(
            Row("Confirm before stopping", "Ask before a recording is stopped from the tray or the status window.", _chkConfirmStop, Glyphs.Stop)));
        general.Controls.Add(Section("Meetings"));
        general.Controls.Add(Card(
            Row("When a call starts", "Teams, Zoom, Slack, Discord or a browser starts using the microphone while you're not recording.", _cmbMeetingMode, Glyphs.Headphones),
            Row("Offer to stop when the call ends", "Only for recordings that were started because of that call.", _chkPromptStopMeeting, Glyphs.Stop)));
        general.Controls.Add(Section("Notifications"));
        general.Controls.Add(Card(
            Row("Audio device changes", "Notify when the microphone or speakers switch. Recording always continues.", _chkNotifyDevice, Glyphs.Headphones),
            Row("Storage events", "Notify when recording moves to another drive or space runs low.", _chkNotifyStorage, Glyphs.HardDrive)));
        general.Controls.Add(Section("Appearance"));
        general.Controls.Add(Card(
            Row("Theme", "Choose light or dark, or follow the app mode set in Windows.", _cmbTheme, Glyphs.Monitor)));

        // Video
        var video = CreatePage("Video", "Frame rate, quality and the encoder used to compress your screen.");
        video.Controls.Add(Section("Capture"));
        video.Controls.Add(Card(
            Row("Monitor", "Which screen to record when you have more than one connected.", _cmbMonitor, Glyphs.Monitor),
            Row("Frame rate", "15 fps keeps files small and is smooth enough for screen sharing and slides.", _cmbFrameRate, Glyphs.Video),
            Row("Quality", "Higher quality keeps small text crisp but creates larger files.", _cmbQuality, Glyphs.Monitor),
            Row("Capture mouse cursor", "Show the pointer in recordings.", _chkCaptureCursor),
            Row("Downscale to 1080p", "Recommended for 4K screens. Uses more CPU.", _chkDownscale)));
        video.Controls.Add(Section("Encoder"));
        _encoderRow = Row("Video encoder", "Auto picks the fastest encoder that works on this PC.", _cmbEncoder, Glyphs.Speed);
        _btnRedetectEncoder.Size = new Size(128, 32);
        _btnRedetectEncoder.Click += async (_, _) => await RedetectEncoderAsync().ConfigureAwait(true);
        video.Controls.Add(Card(
            _encoderRow,
            Row("Hardware encoder test", "Tries every encoder on this PC and keeps the fastest one that works. Takes up to a minute.", _btnRedetectEncoder, Glyphs.Diagnostic)));

        _lstProbeResults.Columns.Add("Encoder", 140);
        _lstProbeResults.Columns.Add("Result", 110);
        _lstProbeResults.Columns.Add("Time", 80, HorizontalAlignment.Right);
        _lstProbeResults.Columns.Add("Details", 240);
        _lstProbeResults.Height = 200;
        _lstProbeResults.EmptyText = "Run the hardware encoder test to see which encoders work on this PC.";
        _lstProbeResults.CellPainter = PaintProbeCell;
        var probeCard = new CardPanel { Padding = new Padding(1, 6, 1, 6), Spacing = 0 };
        probeCard.Controls.Add(_lstProbeResults);
        video.Controls.Add(probeCard);

        // Audio
        var audio = CreatePage("Audio", "Which microphone and speakers are recorded, and how loud.");
        audio.Controls.Add(Section("Sources"));
        _micDeviceRow = Row("Microphone device", "Only used while \"A specific microphone\" is selected above.", _cmbMicDevice, Glyphs.Microphone);
        _outputDeviceRow = Row("Output device", "Only used while \"A specific output device\" is selected above.", _cmbOutputDevice, Glyphs.Volume);
        audio.Controls.Add(Card(
            Row("Microphone", "Recommended: records the microphone your call is using (Teams, Zoom, a browser), otherwise the Windows default.", _cmbMicMode, Glyphs.Microphone),
            _micDeviceRow,
            Row("System audio", "Recommended: records your default speakers plus any headset or device a call or app is playing to.", _cmbOutputMode, Glyphs.Volume),
            _outputDeviceRow));
        audio.Controls.Add(Section("Levels"));
        _trkMicGain.ValueChanged += (_, _) => _lblMicGainVal.Text = FormatGain(_trkMicGain.Value);
        _trkSysGain.ValueChanged += (_, _) => _lblSysGainVal.Text = FormatGain(_trkSysGain.Value);
        _trkMicGain.AccessibleName = "Microphone gain";
        _trkSysGain.AccessibleName = "System audio gain";
        _vuMic.Size = new Size(260, 20);
        _vuSys.Size = new Size(260, 20);
        audio.Controls.Add(Card(
            Row("Microphone gain", "Boost a quiet microphone or soften a loud one.", GainEditor(_trkMicGain, _lblMicGainVal), Glyphs.Microphone),
            Row("System audio gain", "Balance other participants against your own voice.", GainEditor(_trkSysGain, _lblSysGainVal), Glyphs.Volume),
            Row("Microphone level", "Speak to check that your voice is picked up.", _vuMic),
            Row("System audio level", "Play something to check that computer audio is captured.", _vuSys)));
        audio.Controls.Add(Section("Advanced"));
        audio.Controls.Add(Card(
            Row("Jitter buffer", "Smooths out Bluetooth audio. Higher values are steadier.", _numJitterBuffer),
            Row("Audio / video offset", "Shift the audio if it is out of sync with the picture.", _numAvOffset)));

        // Storage
        var storage = CreatePage("Storage", "Where recordings are saved and how files are organized.");
        storage.Controls.Add(Section("Save locations"));
        storage.Controls.Add(new TextLabel("ScreenVault saves to the first location that has enough free space and moves to the next one automatically if a drive fills up or disconnects.", Typography.Caption, TextTone.Secondary, wrap: true));
        _lstLocations.Height = 172;
        _lstLocations.ItemPainter = PaintLocationItem;
        _lstLocations.SelectedIndexChanged += (_, _) => UpdateLocationButtons();
        _btnAddLocation.Click += (_, _) => AddStorageLocation();
        _btnRemoveLocation.Click += (_, _) => RemoveStorageLocation();
        _btnMoveUp.Click += (_, _) => MoveStorageLocation(-1);
        _btnMoveDown.Click += (_, _) => MoveStorageLocation(1);
        var locationButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(10, 8, 10, 8)
        };
        foreach (var button in new[] { _btnAddLocation, _btnMoveUp, _btnMoveDown, _btnRemoveLocation })
        {
            // AutoSize (not an explicit size) so the width is computed after DPI scaling.
            button.AutoSize = true;
            button.Margin = new Padding(0, 0, 8, 0);
            locationButtons.Controls.Add(button);
        }

        var locationsCard = new CardPanel { Padding = new Padding(1, 6, 1, 0), Spacing = 0, Dividers = true };
        locationsCard.Controls.Add(_lstLocations);
        locationsCard.Controls.Add(locationButtons);
        storage.Controls.Add(locationsCard);

        storage.Controls.Add(Section("Files"));
        storage.Controls.Add(Card(
            Row("Split recordings every", "Shorter parts limit what could be affected if something goes wrong.", _cmbSplitMinutes, Glyphs.Cut),
            Row("File format", "MKV is the most robust. MP4 plays everywhere, including phones.", _cmbOutputFormat, Glyphs.Video),
            Row("Keep raw .ts files", "Keep the live recording files after conversion. Uses more space.", _chkKeepTs),
            Row("Return to the primary drive", "Switch back once it has enough free space again (10 GB buffer).", _chkFailback)));
        storage.Controls.Add(Section("Clean-up"));
        _chkRetention.CheckedChanged += (_, _) => _numRetentionDays.Enabled = _chkRetention.Checked;
        storage.Controls.Add(Card(
            Row("Delete old recordings automatically", "Off by default. Protected recordings are always kept.", _chkRetention, Glyphs.Delete),
            Row("Keep recordings for", null, _numRetentionDays)));
        storage.Controls.Add(Section("When a recording stops"));
        _chkMergeOnSave.CheckedChanged += (_, _) => _chkDeletePartsAfterMerge.Enabled = _chkMergeOnSave.Checked;
        storage.Controls.Add(Card(
            Row("Show the \"Recording saved\" window", "Rename, play or move the recording right after you stop.", _chkShowSavedDialog, Glyphs.Completed),
            Row("Merge parts into one file", "Joins the parts of a session into a single video without re-encoding.", _chkMergeOnSave, Glyphs.Merge),
            Row("Delete parts after merging", "Only after the merged file has been verified.", _chkDeletePartsAfterMerge)));

        // Shortcuts
        var hotkeys = CreatePage("Shortcuts", "Global keyboard shortcuts work in any app. Click a shortcut and press a new key combination.");
        hotkeys.Controls.Add(Card(
            Row("Start / stop & save", null, _txtHkStartStop, Glyphs.Record),
            Row("Mute microphone in recording", null, _txtHkMuteMic, Glyphs.Microphone),
            Row("Add marker", null, _txtHkMarker, Glyphs.Flag),
            Row("Pause / resume", null, _txtHkPause, Glyphs.Pause),
            Row("Show status window", null, _txtHkStatus, Glyphs.Monitor)));
        var btnResetHotkeys = new ModernButton("Restore defaults", ButtonKind.Secondary, Glyphs.Refresh) { Size = new Size(150, 32) };
        btnResetHotkeys.Click += (_, _) =>
        {
            var defaults = new HotkeySettings();
            _txtHkStartStop.Hotkey = defaults.StartStop;
            _txtHkMuteMic.Hotkey = defaults.MuteMic;
            _txtHkMarker.Hotkey = defaults.AddMarker;
            _txtHkPause.Hotkey = defaults.PauseResume;
            _txtHkStatus.Hotkey = defaults.ShowStatus;
        };
        hotkeys.Controls.Add(Card(Row("Default shortcuts", "Ctrl + Alt + Shift with R, X, M, P and S.", btnResetHotkeys, Glyphs.Keyboard)));

        // Advanced
        var advanced = CreatePage("Advanced", "Troubleshooting tools and options for experienced users.");
        advanced.Controls.Add(Section("FFmpeg"));
        advanced.Controls.Add(Card(Row("FFmpeg location", "Leave empty to use the FFmpeg that ships with ScreenVault.", FfmpegEditor(), Glyphs.Folder)));
        advanced.Controls.Add(Section("Troubleshooting"));
        var btnOpenLogs = new ModernButton("Open folder", ButtonKind.Secondary, Glyphs.FolderOpen) { Size = new Size(130, 32) };
        btnOpenLogs.Click += (_, _) => OpenLogsFolder();
        var btnExportDiagnostics = new ModernButton("Export…", ButtonKind.Secondary, Glyphs.Export) { Size = new Size(130, 32) };
        btnExportDiagnostics.Click += (_, _) => ExportDiagnostics();
        advanced.Controls.Add(Card(
            Row("Log detail", "Use Debug only while investigating a problem.", _cmbLogLevel, Glyphs.Diagnostic),
            Row("Log files", "Open the folder that contains ScreenVault's logs.", btnOpenLogs),
            Row("Diagnostics package", "Creates a zip with logs, settings and recent sessions on your Desktop.", btnExportDiagnostics)));
        advanced.Controls.Add(Section("Maintenance"));
        var btnRerunWizard = new ModernButton("Run wizard…", ButtonKind.Secondary) { Size = new Size(130, 32) };
        btnRerunWizard.Click += (_, _) => RerunWizard();
        var btnResetDefaults = new ModernButton("Reset…", ButtonKind.Destructive, Glyphs.Refresh) { Size = new Size(130, 32) };
        btnResetDefaults.Click += (_, _) => ResetAllDefaults();
        advanced.Controls.Add(Card(
            Row("Setup wizard", "Walk through storage, audio check and startup options again.", btnRerunWizard),
            Row("Reset all settings", "Restore every setting to its default value (recordings are not touched).", btnResetDefaults)));

        // About
        var about = CreatePage("About", "Version and install details.");
        about.Controls.Add(AboutCard());

        _pages.AddRange([general, video, audio, storage, hotkeys, advanced, about]);
        _audioPageIndex = _pages.IndexOf(audio);
        foreach (var page in _pages)
        {
            host.Controls.Add(page);
        }

        // ── Footer ───────────────────────────────────────────────────────────────────
        var footer = new SurfacePanel { Size = new Size(688, 64), Dock = DockStyle.Bottom, TopDivider = true };
        _lblValidation.Bounds = new Rectangle(24, 22, 300, 20);
        _lblValidation.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        // This window is not modal, so a DialogResult alone would not close it.
        var btnCancel = new ModernButton("Cancel", ButtonKind.Secondary) { Bounds = new Rectangle(344, 16, 96, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnCancel.Click += (_, _) => Close();
        var btnApply = new ModernButton("Apply", ButtonKind.Secondary) { Bounds = new Rectangle(448, 16, 96, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnApply.Click += (_, _) => SaveWorkingCopy();
        var btnSave = new ModernButton("Save", ButtonKind.Primary, Glyphs.CheckMark) { Bounds = new Rectangle(552, 16, 112, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnSave.Click += (_, _) =>
        {
            if (SaveWorkingCopy())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        footer.Controls.AddRange([_lblValidation, btnCancel, btnApply, btnSave]);
        AcceptButton = btnSave;
        CancelButton = btnCancel;

        // ── Sidebar ──────────────────────────────────────────────────────────────────
        var sidebar = new SurfacePanel { Surface = SurfaceKind.Sidebar, Size = new Size(232, 660), Dock = DockStyle.Left };
        var sidebarTitle = new TextLabel("Settings", Typography.Display) { Location = new Point(22, 20) };
        _nav = new NavigationList { Bounds = new Rectangle(10, 76, 212, 290), AccessibleName = "Settings sections" };
        _nav.AddItem("General", Glyphs.Settings);
        _nav.AddItem("Video", Glyphs.Video);
        _nav.AddItem("Audio", Glyphs.Volume);
        _nav.AddItem("Storage", Glyphs.HardDrive);
        _nav.AddItem("Shortcuts", Glyphs.Keyboard);
        _nav.AddItem("Advanced", Glyphs.Diagnostic);
        _nav.AddItem("About", Glyphs.Info);
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        sidebar.Controls.AddRange([sidebarTitle, _nav]);

        Controls.Add(host);
        Controls.Add(footer);
        Controls.Add(sidebar);

        _meterTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _meterTimer.Tick += (_, _) => UpdateMeters();

        FormClosing += (_, _) =>
        {
            _meterTimer.Stop();
            _audioEngine?.SetMonitoring(this, false);
        };

        LoadSettingsIntoUi();
        ShowPage(0);

        ResumeLayout(false);
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _meterTimer.Dispose();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    // ── Layout helpers ───────────────────────────────────────────────────────────────

    private static StackPanel CreatePage(string title, string subtitle)
    {
        var page = new StackPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(32, 26, 32, 32),
            Spacing = 8,
            Visible = false
        };
        page.Controls.Add(new TextLabel(title, Typography.Display));
        page.Controls.Add(new TextLabel(subtitle, Typography.Body, TextTone.Secondary, wrap: true) { Margin = new Padding(0, 0, 0, 6) });
        return page;
    }

    private static TextLabel Section(string text) => new(text, Typography.BodyStrong) { Margin = new Padding(2, 14, 0, 0) };

    private static CardPanel Card(params Control[] rows)
    {
        var card = new CardPanel { Dividers = true, Padding = new Padding(0), Spacing = 0 };
        card.Controls.AddRange(rows);
        return card;
    }

    private static SettingRow Row(string title, string? description, Control? control, char glyph = Glyphs.None) => new(title, description, control, glyph);

    /// <summary>Lists the monitors Windows currently reports, in <see cref="Screen.AllScreens"/> order —
    /// the same order FFmpeg's ddagrab source (<c>output_idx</c>) uses to number outputs.</summary>
    private void PopulateMonitorList()
    {
        _cmbMonitor.Items.Clear();
        _monitorIndexValues.Clear();

        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var bounds = screens[i].Bounds;
            var label = $"Monitor {i + 1} — {bounds.Width}×{bounds.Height}" + (screens[i].Primary ? " (Primary)" : string.Empty);
            _cmbMonitor.Items.Add(label);
            _monitorIndexValues.Add(i);
        }

        if (_monitorIndexValues.Count == 0)
        {
            // Design-time / no display attached: still offer the default so the combo isn't empty.
            _cmbMonitor.Items.Add("Monitor 1 (Primary)");
            _monitorIndexValues.Add(0);
        }
    }

    private void PopulateAudioDeviceLists()
    {
        _cmbMicDevice.Items.Clear();
        _micDeviceIds.Clear();
        _cmbMicDevice.Items.Add("(no microphones found)");
        _micDeviceIds.Add(null);
        foreach (var device in ScreenVault.Core.Audio.AudioDeviceLister.ListCaptureDevices())
        {
            if (_micDeviceIds.Count == 1)
            {
                _cmbMicDevice.Items.Clear();
                _micDeviceIds.Clear();
            }

            _cmbMicDevice.Items.Add(device.Name);
            _micDeviceIds.Add(device.Id);
        }

        _cmbOutputDevice.Items.Clear();
        _outputDeviceIds.Clear();
        _cmbOutputDevice.Items.Add("(no output devices found)");
        _outputDeviceIds.Add(null);
        foreach (var device in ScreenVault.Core.Audio.AudioDeviceLister.ListRenderDevices())
        {
            if (_outputDeviceIds.Count == 1)
            {
                _cmbOutputDevice.Items.Clear();
                _outputDeviceIds.Clear();
            }

            _cmbOutputDevice.Items.Add(device.Name);
            _outputDeviceIds.Add(device.Id);
        }
    }

    private void UpdateAudioDeviceRowVisibility()
    {
        _micDeviceRow.Visible = ValueAt(_micModeValues, _cmbMicMode.SelectedIndex, MicMode.DefaultCommunications) == MicMode.Specific;
        _outputDeviceRow.Visible = ValueAt(_outputModeValues, _cmbOutputMode.SelectedIndex, OutputMode.DefaultPlusCommunications) == OutputMode.Specific;
    }

    /// <summary>Selects the device with <paramref name="deviceId"/>, adding it as an extra ("unavailable
    /// now") entry when it isn't in the current, live device list — so saving never silently changes it.</summary>
    private static int SelectDevice(ModernComboBox combo, List<string?> deviceIds, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return deviceIds.Count > 0 ? 0 : -1;
        }

        var index = deviceIds.FindIndex(id => string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            deviceIds.Add(deviceId);
            combo.Items.Add("(previously chosen device — not connected)");
            index = deviceIds.Count - 1;
        }

        return index;
    }

    private static Panel GainEditor(ModernSlider slider, TextLabel valueLabel)
    {
        var panel = new Panel { Size = new Size(260, 32) };
        slider.Bounds = new Rectangle(0, 2, 200, 28);
        valueLabel.AutoSize = false;
        valueLabel.TextAlign = ContentAlignment.MiddleRight;
        valueLabel.Bounds = new Rectangle(204, 6, 56, 20);
        panel.Controls.Add(slider);
        panel.Controls.Add(valueLabel);
        return panel;
    }

    private Panel FfmpegEditor()
    {
        var panel = new Panel { Size = new Size(380, 32) };
        _txtFfmpegPath.Bounds = new Rectangle(0, 0, 272, 32);
        var browse = new ModernButton("Browse…", ButtonKind.Secondary) { Bounds = new Rectangle(280, 0, 100, 32) };
        browse.Click += (_, _) => BrowseFfmpeg();
        panel.Controls.Add(_txtFfmpegPath);
        panel.Controls.Add(browse);
        return panel;
    }

    private CardPanel AboutCard()
    {
        var info = GetAboutInfo();
        var card = new CardPanel { Padding = new Padding(0), Spacing = 0, Dividers = true };

        var hero = new Panel { Height = 104 };
        var appIcon = AppIcon.Get();
        if (appIcon != null)
        {
            using var large = new Icon(appIcon, 64, 64);
            hero.Controls.Add(new PictureBox { Image = large.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(20, 20, 64, 64) });
        }

        hero.Controls.Add(new TextLabel("ScreenVault", Typography.Title) { Location = new Point(100, 24) });
        hero.Controls.Add(new TextLabel($"Version {info.Version}", Typography.Body, TextTone.Secondary) { Location = new Point(100, 52) });
        hero.Controls.Add(new TextLabel("Always-on, crash-proof screen & meeting recorder", Typography.Caption, TextTone.Tertiary) { Location = new Point(100, 74) });
        card.Controls.Add(hero);

        card.Controls.Add(Row("Install type", null, Value(info.Scope), Glyphs.Shield));
        var pathValue = Value(info.InstallPath);
        _toolTip.SetToolTip(pathValue, info.InstallPath);
        card.Controls.Add(Row("Install location", null, pathValue, Glyphs.Folder));
        card.Controls.Add(Row("FFmpeg", "Bundled encoder (GPL). See THIRD_PARTY_NOTICES.txt.", Value(info.FfmpegVersion), Glyphs.Video));
        card.Controls.Add(Row("Privacy", "ScreenVault never connects to the internet. Your recordings stay on your drives and the tray icon is always visible while recording.", null, Glyphs.Lock));
        return card;

        static TextLabel Value(string text) => new(text, Typography.Body, TextTone.Secondary) { AutoSize = false, AutoEllipsis = true, Size = new Size(320, 20), TextAlign = ContentAlignment.MiddleRight };
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].Visible = i == index;
        }

        var isAudio = index == _audioPageIndex;
        _audioEngine?.SetMonitoring(this, isAudio);
        if (isAudio && _audioEngine != null)
        {
            _meterTimer.Start();
        }
        else
        {
            _meterTimer.Stop();
        }
    }

    private void UpdateMeters()
    {
        if (_audioEngine == null)
        {
            return;
        }

        var status = _audioEngine.GetStatus();
        float micPeak = -60f, micRms = -60f, sysPeak = -60f, sysRms = -60f;
        foreach (var device in status.ActiveDevices)
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

        _vuMic.IsMuted = status.IsMicMuted;
        _vuMic.SetLevels(micPeak, micPeak, micRms, micRms);
        _vuSys.SetLevels(sysPeak, sysPeak, sysRms, sysRms);
    }

    private static string FormatGain(int value) => value > 0
        ? string.Create(CultureInfo.CurrentCulture, $"+{value} dB")
        : string.Create(CultureInfo.CurrentCulture, $"{value} dB");

    // ── Load / save ──────────────────────────────────────────────────────────────────

    private void LoadSettingsIntoUi()
    {
        // General
        _chkStartWithWindows.Checked = _workingCopy.General.StartWithWindows;
        _chkAutoStartRecording.Checked = _workingCopy.General.StartRecordingOnLaunch;
        _chkStartMinimized.Checked = _workingCopy.General.MinimizeToTrayOnLaunch;
        _numStartupDelay.Value = Math.Clamp(_workingCopy.General.StartupDelaySeconds, 0, 120);
        _chkConfirmStop.Checked = _workingCopy.General.ConfirmBeforeStop;
        _chkNotifyDevice.Checked = _workingCopy.General.Notifications.DeviceSwitch;
        _chkNotifyStorage.Checked = _workingCopy.General.Notifications.Storage;
        _cmbTheme.SelectedIndex = _workingCopy.General.Theme switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.Dark => 2,
            _ => 0
        };
        _cmbMeetingMode.SelectedIndex = _workingCopy.MeetingDetection.Mode switch
        {
            MeetingDetectionMode.AutoStart => 1,
            MeetingDetectionMode.Off => 2,
            _ => 0
        };
        _chkPromptStopMeeting.Checked = _workingCopy.MeetingDetection.PromptStopWhenMeetingEnds;

        // Video
        _cmbMonitor.SelectedIndex = SelectValue(_cmbMonitor, _monitorIndexValues, _workingCopy.Video.MonitorIndex,
            idx => $"Monitor {idx + 1} (not currently connected)");

        _cmbFrameRate.SelectedIndex = SelectValue(_cmbFrameRate, _frameRateValues, _workingCopy.Video.FrameRate,
            fps => string.Create(CultureInfo.CurrentCulture, $"{fps} fps (current)"));

        _cmbQuality.SelectedIndex = _workingCopy.Video.Quality switch
        {
            VideoQuality.Small => 0,
            VideoQuality.High => 2,
            _ => 1
        };

        _cmbEncoder.SelectedItem = _workingCopy.Video.Encoder;
        if (_cmbEncoder.SelectedIndex < 0)
        {
            _cmbEncoder.SelectedIndex = 0;
        }

        _chkCaptureCursor.Checked = _workingCopy.Video.CaptureCursor;
        _chkDownscale.Checked = _workingCopy.Video.DownscaleTo1080p;
        UpdateEncoderDescription(_workingCopy.Video.DetectedEncoderProfile);

        // Audio
        _cmbMicMode.SelectedIndex = SelectValue(_cmbMicMode, _micModeValues, _workingCopy.Audio.MicMode,
            _ => "A specific microphone (chosen earlier)");
        _cmbOutputMode.SelectedIndex = SelectValue(_cmbOutputMode, _outputModeValues, _workingCopy.Audio.OutputMode,
            mode => mode == OutputMode.AllActive ? "All playback devices" : "A specific output device (chosen earlier)");
        _cmbMicDevice.SelectedIndex = SelectDevice(_cmbMicDevice, _micDeviceIds, _workingCopy.Audio.MicDeviceId);
        _cmbOutputDevice.SelectedIndex = SelectDevice(_cmbOutputDevice, _outputDeviceIds, _workingCopy.Audio.OutputDeviceId);
        UpdateAudioDeviceRowVisibility();
        _trkMicGain.Value = (int)Math.Clamp(_workingCopy.Audio.MicGainDb, -20, 20);
        _lblMicGainVal.Text = FormatGain(_trkMicGain.Value);
        _trkSysGain.Value = (int)Math.Clamp(_workingCopy.Audio.SystemGainDb, -20, 20);
        _lblSysGainVal.Text = FormatGain(_trkSysGain.Value);
        _numJitterBuffer.Value = Math.Clamp(_workingCopy.Audio.JitterTargetMs, 30, 500);
        _numAvOffset.Value = Math.Clamp(_workingCopy.Audio.AvOffsetMs, -500, 500);

        // Storage
        RefreshLocationList(selectIndex: 0);

        _cmbSplitMinutes.SelectedIndex = SelectValue(_cmbSplitMinutes, _splitMinuteValues, _workingCopy.Storage.SplitMinutes,
            minutes => string.Create(CultureInfo.CurrentCulture, $"{minutes} minutes (current)"));

        _cmbOutputFormat.SelectedIndex = _workingCopy.Storage.OutputFormat switch
        {
            OutputContainerFormat.Mp4 => 1,
            OutputContainerFormat.Ts => 2,
            _ => 0
        };

        _chkKeepTs.Checked = _workingCopy.Storage.KeepTsAfterRemux;
        _chkFailback.Checked = _workingCopy.Storage.FailbackToPrimary;
        _chkRetention.Checked = _workingCopy.Storage.Retention.Enabled;
        _numRetentionDays.Value = Math.Clamp(_workingCopy.Storage.Retention.KeepDays, 1, 365);
        _numRetentionDays.Enabled = _chkRetention.Checked;

        // Saving
        _chkShowSavedDialog.Checked = _workingCopy.Saving.ShowSavedDialog;
        _chkMergeOnSave.Checked = _workingCopy.Saving.MergeOnSave;
        _chkDeletePartsAfterMerge.Checked = _workingCopy.Saving.DeletePartsAfterMerge;
        _chkDeletePartsAfterMerge.Enabled = _chkMergeOnSave.Checked;

        // Hotkeys
        _txtHkStartStop.Hotkey = _workingCopy.Hotkeys.StartStop;
        _txtHkMuteMic.Hotkey = _workingCopy.Hotkeys.MuteMic;
        _txtHkMarker.Hotkey = _workingCopy.Hotkeys.AddMarker;
        _txtHkPause.Hotkey = _workingCopy.Hotkeys.PauseResume;
        _txtHkStatus.Hotkey = _workingCopy.Hotkeys.ShowStatus;

        // Advanced
        _txtFfmpegPath.Text = _workingCopy.Advanced.FfmpegPath ?? string.Empty;
        _cmbLogLevel.SelectedItem = _workingCopy.Advanced.LogLevel;
        if (_cmbLogLevel.SelectedIndex < 0)
        {
            _cmbLogLevel.SelectedIndex = 1;
        }

        // Video probe details
        RefreshProbeDetails(EncoderProbe.LastResult?.Details);
        _lblValidation.Text = string.Empty;
    }

    private void RefreshProbeDetails(IReadOnlyList<ProfileProbeStatus>? details)
    {
        _lstProbeResults.BeginUpdate();
        _lstProbeResults.Items.Clear();
        if (details != null)
        {
            foreach (var status in details)
            {
                var item = new ListViewItem(status.ProfileName) { Tag = status.Success };
                item.SubItems.Add(status.Success ? "Works" : "Not available");
                item.SubItems.Add(string.Create(CultureInfo.CurrentCulture, $"{status.Duration.TotalMilliseconds:F0} ms"));
                item.SubItems.Add(status.Reason);
                _lstProbeResults.Items.Add(item);
            }
        }

        _lstProbeResults.EndUpdate();
    }

    private bool PaintProbeCell(DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 1 || e.Item?.Tag is not bool success)
        {
            return false;
        }

        var p = Theme.Current;
        var scale = Draw.Scale(_lstProbeResults);
        var text = e.SubItem?.Text ?? string.Empty;
        var tone = success ? Tone.Success : Tone.Neutral;
        var textWidth = TextRenderer.MeasureText(text, Typography.CaptionStrong).Width;
        var pill = new RectangleF(e.Bounds.X + (10 * scale), e.Bounds.Y + ((e.Bounds.Height - (22 * scale)) / 2f), textWidth + (16 * scale), 22 * scale);
        Draw.PrepareHighQuality(e.Graphics);
        Draw.FillRounded(e.Graphics, p.Soft(tone), pill, pill.Height / 2f);
        TextRenderer.DrawText(e.Graphics, text, Typography.CaptionStrong, Rectangle.Round(pill), p.Foreground(tone),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return true;
    }

    private void UpdateEncoderDescription(string? detectedProfile)
    {
        _encoderRow.Description = string.IsNullOrEmpty(detectedProfile)
            ? "Auto picks the fastest encoder that works on this PC."
            : $"Auto picks the fastest encoder that works on this PC. Detected: {detectedProfile}.";
    }

    private bool SaveWorkingCopy()
    {
        // Harvest UI values into _workingCopy
        _workingCopy.General.StartWithWindows = _chkStartWithWindows.Checked;
        _workingCopy.General.StartRecordingOnLaunch = _chkAutoStartRecording.Checked;
        _workingCopy.General.MinimizeToTrayOnLaunch = _chkStartMinimized.Checked;
        _workingCopy.General.StartupDelaySeconds = (int)_numStartupDelay.Value;
        _workingCopy.General.ConfirmBeforeStop = _chkConfirmStop.Checked;
        _workingCopy.General.Notifications.DeviceSwitch = _chkNotifyDevice.Checked;
        _workingCopy.General.Notifications.Storage = _chkNotifyStorage.Checked;
        _workingCopy.General.Theme = _cmbTheme.SelectedIndex switch
        {
            1 => AppThemeMode.Light,
            2 => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };
        _workingCopy.MeetingDetection.Mode = _cmbMeetingMode.SelectedIndex switch
        {
            1 => MeetingDetectionMode.AutoStart,
            2 => MeetingDetectionMode.Off,
            _ => MeetingDetectionMode.Ask
        };
        _workingCopy.MeetingDetection.PromptStopWhenMeetingEnds = _chkPromptStopMeeting.Checked;

        _workingCopy.Video.MonitorIndex = ValueAt(_monitorIndexValues, _cmbMonitor.SelectedIndex, 0);
        _workingCopy.Video.FrameRate = ValueAt(_frameRateValues, _cmbFrameRate.SelectedIndex, 15);

        _workingCopy.Video.Quality = _cmbQuality.SelectedIndex switch
        {
            0 => VideoQuality.Small,
            2 => VideoQuality.High,
            _ => VideoQuality.Balanced
        };

        _workingCopy.Video.Encoder = _cmbEncoder.SelectedItem?.ToString() ?? "Auto";
        _workingCopy.Video.CaptureCursor = _chkCaptureCursor.Checked;
        _workingCopy.Video.DownscaleTo1080p = _chkDownscale.Checked;

        _workingCopy.Audio.MicMode = ValueAt(_micModeValues, _cmbMicMode.SelectedIndex, MicMode.DefaultCommunications);
        _workingCopy.Audio.OutputMode = ValueAt(_outputModeValues, _cmbOutputMode.SelectedIndex, OutputMode.DefaultPlusCommunications);
        _workingCopy.Audio.MicDeviceId = ValueAt(_micDeviceIds, _cmbMicDevice.SelectedIndex, null);
        _workingCopy.Audio.OutputDeviceId = ValueAt(_outputDeviceIds, _cmbOutputDevice.SelectedIndex, null);

        _workingCopy.Audio.MicGainDb = _trkMicGain.Value;
        _workingCopy.Audio.SystemGainDb = _trkSysGain.Value;
        _workingCopy.Audio.JitterTargetMs = (int)_numJitterBuffer.Value;
        _workingCopy.Audio.AvOffsetMs = (int)_numAvOffset.Value;

        _workingCopy.Storage.SplitMinutes = ValueAt(_splitMinuteValues, _cmbSplitMinutes.SelectedIndex, 10);

        _workingCopy.Storage.OutputFormat = _cmbOutputFormat.SelectedIndex switch
        {
            1 => OutputContainerFormat.Mp4,
            2 => OutputContainerFormat.Ts,
            _ => OutputContainerFormat.Mkv
        };

        _workingCopy.Storage.KeepTsAfterRemux = _chkKeepTs.Checked;
        _workingCopy.Storage.FailbackToPrimary = _chkFailback.Checked;
        _workingCopy.Storage.Retention.Enabled = _chkRetention.Checked;
        _workingCopy.Storage.Retention.KeepDays = (int)_numRetentionDays.Value;

        // Saving
        _workingCopy.Saving.ShowSavedDialog = _chkShowSavedDialog.Checked;
        _workingCopy.Saving.MergeOnSave = _chkMergeOnSave.Checked;
        _workingCopy.Saving.DeletePartsAfterMerge = _chkDeletePartsAfterMerge.Checked;

        // Hotkeys
        _workingCopy.Hotkeys.StartStop = _txtHkStartStop.Hotkey;
        _workingCopy.Hotkeys.MuteMic = _txtHkMuteMic.Hotkey;
        _workingCopy.Hotkeys.AddMarker = _txtHkMarker.Hotkey;
        _workingCopy.Hotkeys.PauseResume = _txtHkPause.Hotkey;
        _workingCopy.Hotkeys.ShowStatus = _txtHkStatus.Hotkey;

        _workingCopy.Advanced.FfmpegPath = string.IsNullOrWhiteSpace(_txtFfmpegPath.Text) ? null : _txtFfmpegPath.Text.Trim();
        _workingCopy.Advanced.LogLevel = _cmbLogLevel.SelectedItem?.ToString() ?? "Information";

        var errors = new List<string>();
        var validationResult = SettingsValidator.Validate(_workingCopy);
        if (!validationResult.IsValid)
        {
            errors.AddRange(validationResult.Errors);
        }

        if (_workingCopy.Audio.MicMode == MicMode.Specific && string.IsNullOrWhiteSpace(_workingCopy.Audio.MicDeviceId))
        {
            errors.Add("Choose a microphone, or switch back to a Windows default.");
        }

        if (_workingCopy.Audio.OutputMode == OutputMode.Specific && string.IsNullOrWhiteSpace(_workingCopy.Audio.OutputDeviceId))
        {
            errors.Add("Choose an output device, or switch back to a Windows default.");
        }

        var shortcuts = new[] { _workingCopy.Hotkeys.StartStop, _workingCopy.Hotkeys.MuteMic, _workingCopy.Hotkeys.AddMarker, _workingCopy.Hotkeys.PauseResume, _workingCopy.Hotkeys.ShowStatus };
        if (shortcuts.Where(s => !string.IsNullOrWhiteSpace(s)).GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            errors.Add("Two actions use the same keyboard shortcut.");
        }

        if (errors.Count > 0)
        {
            _lblValidation.Text = string.Join(" ", errors);
            _toolTip.SetToolTip(_lblValidation, string.Join(Environment.NewLine, errors));
            return false;
        }

        try
        {
            _settingsService.Save(_workingCopy);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not save settings.");
            _lblValidation.Text = $"Could not save: {ex.Message}";
            _toolTip.SetToolTip(_lblValidation, ex.Message);
            return false;
        }

        _lblValidation.Text = string.Empty;
        Theme.SetMode(_workingCopy.General.Theme);

        // The saved object is now the app's live settings: keep editing a private copy.
        _workingCopy = CloneSettings(_workingCopy);

        // Update StartWithWindows registry if changed
        try
        {
            StartWithWindows.SetEnabled(_workingCopy.General.StartWithWindows, _workingCopy.General.StartRecordingOnLaunch);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not update StartWithWindows registry.");
        }

        return true;
    }

    private async Task RedetectEncoderAsync()
    {
        _btnRedetectEncoder.Enabled = false;
        _btnRedetectEncoder.Text = "Testing…";
        try
        {
            var customPath = string.IsNullOrWhiteSpace(_txtFfmpegPath.Text) ? null : _txtFfmpegPath.Text.Trim();
            var paths = await Task.Run(() => new FfmpegLocator().Locate(customPath)).ConfigureAwait(true);
            var result = await EncoderProbe.ProbeAsync(paths.FfmpegPath).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            _workingCopy.Video.DetectedEncoderProfile = result.ProfileName;
            _workingCopy.Video.EncoderFingerprint = result.Fingerprint;
            RefreshProbeDetails(result.Details);
            UpdateEncoderDescription(result.ProfileName);
            ModernDialog.Success(this, "Encoder test finished", $"The fastest working encoder on this PC is {result.ProfileName}. Save to keep this result.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Encoder probe failed.");
            if (!IsDisposed)
            {
                ModernDialog.Warning(this, "Encoder test failed", ex.Message);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _btnRedetectEncoder.Text = "Detect now";
                _btnRedetectEncoder.Enabled = true;
            }
        }
    }

    // ── Storage locations ────────────────────────────────────────────────────────────

    private void RefreshLocationList(int selectIndex)
    {
        _freeSpaceCache.Clear();
        _lstLocations.BeginUpdate();
        _lstLocations.Items.Clear();
        foreach (var location in _workingCopy.Storage.Locations)
        {
            _lstLocations.Items.Add(location);
        }

        _lstLocations.EndUpdate();
        if (_lstLocations.Items.Count > 0)
        {
            _lstLocations.SelectedIndex = Math.Clamp(selectIndex, 0, _lstLocations.Items.Count - 1);
        }

        UpdateLocationButtons();
    }

    private void UpdateLocationButtons()
    {
        var index = _lstLocations.SelectedIndex;
        var count = _workingCopy.Storage.Locations.Count;
        _btnRemoveLocation.Enabled = index >= 0 && count > 1;
        _btnMoveUp.Enabled = index > 0;
        _btnMoveDown.Enabled = index >= 0 && index < count - 1;
    }

    private void PaintLocationItem(DrawItemEventArgs e, object item)
    {
        if (item is not StorageLocationConfig location)
        {
            return;
        }

        var p = Theme.Current;
        var g = e.Graphics;
        var scale = Draw.Scale(_lstLocations);
        var expanded = Environment.ExpandEnvironmentVariables(location.Path);
        var role = e.Index == 0 ? "Primary" : e.Index == 1 ? "Backup" : $"Backup {e.Index}";
        var tone = e.Index == 0 ? Tone.Accent : Tone.Neutral;

        var left = e.Bounds.X + (int)(16 * scale);
        var top = e.Bounds.Y + (int)(8 * scale);
        Glyphs.Draw(g, Glyphs.HardDrive, new Rectangle(left, e.Bounds.Y, (int)(20 * scale), e.Bounds.Height), p.TextSecondary, 12f);
        left += Glyphs.Available ? (int)(34 * scale) : 0;

        var pillWidth = TextRenderer.MeasureText(role, Typography.CaptionStrong).Width + (int)(16 * scale);
        var pill = new RectangleF(left, top, pillWidth, 20 * scale);
        Draw.PrepareHighQuality(g);
        Draw.FillRounded(g, p.Soft(tone), pill, pill.Height / 2f);
        TextRenderer.DrawText(g, role, Typography.CaptionStrong, Rectangle.Round(pill), p.Foreground(tone),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        var pathLeft = left + pillWidth + (int)(10 * scale);
        TextRenderer.DrawText(g, expanded, Typography.Body, new Rectangle(pathLeft, top, e.Bounds.Right - pathLeft - (int)(12 * scale), (int)(20 * scale)), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);

        var detail = $"Keeps at least {location.MinFreeGb} GB free";
        var free = FreeSpace(expanded);
        if (free >= 0)
        {
            detail += $" · {StorageMeterList.FormatBytes(free)} available now";
        }
        else
        {
            detail += " · drive not available";
        }

        if (!location.Enabled)
        {
            detail += " · disabled";
        }

        TextRenderer.DrawText(g, detail, Typography.Caption, new Rectangle(left, top + (int)(24 * scale), e.Bounds.Right - left - (int)(12 * scale), (int)(18 * scale)), p.TextSecondary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private long FreeSpace(string path)
    {
        if (_freeSpaceCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        long free = -1;
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    free = drive.AvailableFreeSpace;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Could not read free space for {Path}", path);
        }

        _freeSpaceCache[path] = free;
        return free;
    }

    private void AddStorageLocation()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose a folder for ScreenVault recordings", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _workingCopy.Storage.Locations.Add(new StorageLocationConfig { Path = dlg.SelectedPath, MinFreeGb = 5, Enabled = true });
            RefreshLocationList(_workingCopy.Storage.Locations.Count - 1);
        }
    }

    private void RemoveStorageLocation()
    {
        var idx = _lstLocations.SelectedIndex;
        if (idx < 0 || idx >= _workingCopy.Storage.Locations.Count)
        {
            return;
        }

        if (_workingCopy.Storage.Locations.Count <= 1)
        {
            ModernDialog.Info(this, "At least one location is needed", "ScreenVault needs somewhere to save recordings. Add another folder before removing this one.");
            return;
        }

        _workingCopy.Storage.Locations.RemoveAt(idx);
        RefreshLocationList(idx);
    }

    private void MoveStorageLocation(int delta)
    {
        var idx = _lstLocations.SelectedIndex;
        var target = idx + delta;
        var locations = _workingCopy.Storage.Locations;
        if (idx < 0 || target < 0 || target >= locations.Count)
        {
            return;
        }

        (locations[idx], locations[target]) = (locations[target], locations[idx]);
        RefreshLocationList(target);
    }

    // ── Advanced actions ─────────────────────────────────────────────────────────────

    private void BrowseFfmpeg()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "ffmpeg.exe|ffmpeg.exe|All files (*.*)|*.*",
            Title = "Locate ffmpeg.exe"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _txtFfmpegPath.Text = dlg.FileName;
        }
    }

    private static void OpenLogsFolder()
    {
        var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenVault", "logs");
        if (!Directory.Exists(logsDir))
        {
            Directory.CreateDirectory(logsDir);
        }

        Process.Start("explorer.exe", $"\"{logsDir}\"");
    }

    private void RerunWizard()
    {
        if (_audioEngine == null)
        {
            ModernDialog.Info(this, "Audio engine is not available", "The setup wizard needs the audio engine to check your microphone.");
            return;
        }

        using var wizard = new FirstRunWizardForm(_settingsService, _audioEngine);
        if (wizard.ShowDialog(this) == DialogResult.OK)
        {
            _workingCopy = CloneSettings(_settingsService.Current);
            LoadSettingsIntoUi();
        }

        ShowPage(_nav.SelectedIndex);
    }

    private void ExportDiagnostics()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var zipPath = Path.Combine(desktop, $"ScreenVault_Diagnostics_{timestamp}.zip");

            var tempDir = Path.Combine(Path.GetTempPath(), $"sv_diag_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. Copy logs
                var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenVault", "logs");
                if (Directory.Exists(logsDir))
                {
                    var destLogs = Path.Combine(tempDir, "logs");
                    Directory.CreateDirectory(destLogs);
                    foreach (var f in Directory.GetFiles(logsDir, "*.log"))
                    {
                        try { File.Copy(f, Path.Combine(destLogs, Path.GetFileName(f)), overwrite: true); } catch { }
                    }
                }

                // 2. Copy current settings
                var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenVault", "settings.json");
                if (File.Exists(settingsPath))
                {
                    File.Copy(settingsPath, Path.Combine(tempDir, "settings.json"), overwrite: true);
                }

                // 3. Copy recent sessions
                var sessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenVault", "sessions");
                if (Directory.Exists(sessionsDir))
                {
                    var destSessions = Path.Combine(tempDir, "sessions");
                    Directory.CreateDirectory(destSessions);
                    var sessionFiles = new DirectoryInfo(sessionsDir).GetFiles("*.json")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Take(5);
                    foreach (var sf in sessionFiles)
                    {
                        try { File.Copy(sf.FullName, Path.Combine(destSessions, sf.Name), overwrite: true); } catch { }
                    }
                }

                // 4. Create zip archive
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

                ModernDialog.Success(this, "Diagnostics exported", $"Saved to your Desktop as {Path.GetFileName(zipPath)}.");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export diagnostics.");
            ModernDialog.Error(this, "Could not export diagnostics", ex.Message);
        }
    }

    private void ResetAllDefaults()
    {
        if (ModernDialog.Confirm(this, "Reset all settings?", "Every setting returns to its default value when you save. Your recordings are not affected.", "Reset", "Cancel", destructive: true, icon: MessageBoxIcon.Warning))
        {
            _workingCopy = AppSettings.CreateDefault();
            LoadSettingsIntoUi();
        }
    }

    /// <summary>Selects <paramref name="value"/>, adding it (with <paramref name="label"/>) if the list lacks it.</summary>
    private static int SelectValue<T>(ModernComboBox combo, List<T> values, T value, Func<T, string> label)
    {
        var index = values.IndexOf(value);
        if (index < 0)
        {
            values.Add(value);
            combo.Items.Add(label(value));
            index = values.Count - 1;
        }

        return index;
    }

    private static T ValueAt<T>(List<T> values, int index, T fallback) =>
        index >= 0 && index < values.Count ? values[index] : fallback;

    private static AppSettings CloneSettings(AppSettings source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? AppSettings.CreateDefault();
    }

    private static (string Version, string Scope, string InstallPath, string FfmpegVersion) GetAboutInfo()
    {
        var version = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "1.2.0";
        var installPath = AppContext.BaseDirectory.TrimEnd('\\');

        string scope = "Per-user";
        if (StartWithWindows.HasHklmRunEntry() ||
            installPath.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
        {
            scope = "All users (machine)";
        }
        else
        {
            var defaultsPath = Path.Combine(AppContext.BaseDirectory, "install-defaults.json");
            if (File.Exists(defaultsPath))
            {
                try
                {
                    var defaults = InstallDefaultsService.LoadDefaults(defaultsPath, new System.IO.Abstractions.FileSystem());
                    if (!string.IsNullOrEmpty(defaults?.InstallScope))
                    {
                        scope = defaults.InstallScope;
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }

        string ffmpegVersion = "ffmpeg (bundled)";
        var ffmpegVersionPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "VERSION.txt");
        if (File.Exists(ffmpegVersionPath))
        {
            try
            {
                var firstLine = File.ReadAllLines(ffmpegVersionPath).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrEmpty(firstLine))
                {
                    ffmpegVersion = firstLine.Trim();
                }
            }
            catch
            {
                ffmpegVersion = "ffmpeg (bundled)";
            }
        }

        return (version, scope, installPath, ffmpegVersion);
    }
}
