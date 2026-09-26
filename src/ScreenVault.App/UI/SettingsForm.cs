using System.Diagnostics;
using System.IO.Compression;
using ScreenVault.App.Platform;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

public sealed class SettingsForm : Form
{
    private readonly ISettingsService _settingsService;
    private AppSettings _workingCopy;

    // General controls
    private readonly CheckBox _chkStartWithWindows;
    private readonly CheckBox _chkAutoStartRecording;
    private readonly CheckBox _chkStartMinimized;
    private readonly NumericUpDown _numStartupDelay;
    private readonly CheckBox _chkConfirmStop;
    private readonly CheckBox _chkNotifyDevice;
    private readonly CheckBox _chkNotifyStorage;

    // Video controls
    private readonly ComboBox _cmbFrameRate;
    private readonly ComboBox _cmbQuality;
    private readonly ComboBox _cmbEncoder;
    private readonly Button _btnRedetectEncoder;
    private readonly CheckBox _chkCaptureCursor;
    private readonly CheckBox _chkDownscale;
    private readonly ListView _lstProbeResults;

    // Audio controls
    private readonly ComboBox _cmbMicMode;
    private readonly ComboBox _cmbOutputMode;
    private readonly TrackBar _trkMicGain;
    private readonly Label _lblMicGainVal;
    private readonly TrackBar _trkSysGain;
    private readonly Label _lblSysGainVal;
    private readonly NumericUpDown _numJitterBuffer;
    private readonly NumericUpDown _numAvOffset;

    // Storage & Saving controls
    private readonly ListBox _lstLocations;
    private readonly Button _btnAddLocation;
    private readonly Button _btnRemoveLocation;
    private readonly ComboBox _cmbSplitMinutes;
    private readonly ComboBox _cmbOutputFormat;
    private readonly CheckBox _chkKeepTs;
    private readonly CheckBox _chkFailback;
    private readonly CheckBox _chkRetention;
    private readonly NumericUpDown _numRetentionDays;
    private readonly CheckBox _chkShowSavedDialog;
    private readonly CheckBox _chkMergeOnSave;
    private readonly CheckBox _chkDeletePartsAfterMerge;

    // Hotkey controls
    private readonly TextBox _txtHkStartStop;
    private readonly TextBox _txtHkMuteMic;
    private readonly TextBox _txtHkMarker;
    private readonly TextBox _txtHkPause;
    private readonly TextBox _txtHkStatus;
    private readonly Button _btnResetHotkeys;

    // Advanced controls
    private readonly TextBox _txtFfmpegPath;
    private readonly Button _btnBrowseFfmpeg;
    private readonly ComboBox _cmbLogLevel;
    private readonly Button _btnOpenLogs;
    private readonly Button _btnResetDefaults;
    private readonly Button _btnRerunWizard;
    private readonly Button _btnExportDiagnostics;
    private readonly IAudioEngine? _audioEngine;

    private readonly Button _btnOk;
    private readonly Button _btnCancel;
    private readonly Button _btnApply;
    private readonly Label _lblValidation;

    public SettingsForm(ISettingsService settingsService, IAudioEngine? audioEngine = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _audioEngine = audioEngine;
        _workingCopy = CloneSettings(_settingsService.Current);

        Text = "ScreenVault Settings";
        var appIcon = AppIcon.Get();
        if (appIcon != null) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 560);

        var tabControl = new TabControl
        {
            Location = new Point(14, 12),
            Size = new Size(592, 475)
        };

        // 1. General Tab
        var tabGeneral = new TabPage("General") { Padding = new Padding(12) };
        var pnlGeneral = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            AutoSize = true,
            AutoScroll = true
        };
        pnlGeneral.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pnlGeneral.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _chkStartWithWindows = new CheckBox { Text = "Start with Windows", AutoSize = true, Margin = new Padding(3, 6, 3, 6), UseMnemonic = false };
        _chkAutoStartRecording = new CheckBox { Text = "Start recording when application launches", AutoSize = true, Margin = new Padding(3, 6, 3, 6), UseMnemonic = false };
        _chkStartMinimized = new CheckBox { Text = "Start minimized to system tray", AutoSize = true, Margin = new Padding(3, 6, 3, 6), UseMnemonic = false };

        var lblDelay = new Label { Text = "Startup delay (seconds):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 6), UseMnemonic = false };
        _numStartupDelay = new NumericUpDown { Minimum = 0, Maximum = 60, Width = 80, MinimumSize = new Size(80, 24), Margin = new Padding(3, 6, 3, 6) };

        _chkConfirmStop = new CheckBox { Text = "Confirm before stopping recording", AutoSize = true, Margin = new Padding(3, 6, 3, 6), UseMnemonic = false };
        _chkNotifyDevice = new CheckBox { Text = "Show balloon notifications on audio device switches", AutoSize = true, Margin = new Padding(3, 6, 3, 6), UseMnemonic = false };
        _chkNotifyStorage = new CheckBox { Text = "Show balloon notifications on storage failover events", AutoSize = true, Margin = new Padding(3, 6, 3, 6), UseMnemonic = false };

        pnlGeneral.Controls.Add(_chkStartWithWindows, 0, 0);
        pnlGeneral.SetColumnSpan(_chkStartWithWindows, 2);

        pnlGeneral.Controls.Add(_chkAutoStartRecording, 0, 1);
        pnlGeneral.SetColumnSpan(_chkAutoStartRecording, 2);

        pnlGeneral.Controls.Add(_chkStartMinimized, 0, 2);
        pnlGeneral.SetColumnSpan(_chkStartMinimized, 2);

        pnlGeneral.Controls.Add(lblDelay, 0, 3);
        pnlGeneral.Controls.Add(_numStartupDelay, 1, 3);

        pnlGeneral.Controls.Add(_chkConfirmStop, 0, 4);
        pnlGeneral.SetColumnSpan(_chkConfirmStop, 2);

        pnlGeneral.Controls.Add(_chkNotifyDevice, 0, 5);
        pnlGeneral.SetColumnSpan(_chkNotifyDevice, 2);

        pnlGeneral.Controls.Add(_chkNotifyStorage, 0, 6);
        pnlGeneral.SetColumnSpan(_chkNotifyStorage, 2);

        tabGeneral.Controls.Add(pnlGeneral);

        // 2. Video Tab
        var tabVideo = new TabPage("Video") { Padding = new Padding(12) };
        var lblFps = new Label { Text = "Frame rate:", Location = new Point(20, 20), AutoSize = true };
        _cmbFrameRate = new ComboBox { Location = new Point(140, 17), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbFrameRate.Items.AddRange(["15 fps (Recommended)", "24 fps", "30 fps"]);

        var lblQuality = new Label { Text = "Quality profile:", Location = new Point(20, 55), AutoSize = true };
        _cmbQuality = new ComboBox { Location = new Point(140, 52), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbQuality.Items.AddRange(["Small (lowest CPU)", "Balanced (Default)", "High (Crisp text)"]);

        var lblEncoder = new Label { Text = "Video Encoder:", Location = new Point(20, 90), AutoSize = true };
        _cmbEncoder = new ComboBox { Location = new Point(140, 87), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbEncoder.Items.AddRange(["Auto", "nvenc-d3d11", "amf-d3d11", "qsv-hwmap", "nvenc-sysmem", "amf-sysmem", "qsv-sysmem", "x264"]);

        _btnRedetectEncoder = new Button { Text = "Re-detect", Location = new Point(310, 85), Width = 90, Height = 26 };
        _btnRedetectEncoder.Click += async (_, _) => await RedetectEncoderAsync().ConfigureAwait(true);

        _chkCaptureCursor = new CheckBox { Text = "Capture mouse cursor", Location = new Point(20, 125), AutoSize = true };
        _chkDownscale = new CheckBox { Text = "Downscale to 1080p if screen is larger (higher CPU)", Location = new Point(20, 150), AutoSize = true };

        var lblProbeHeader = new Label { Text = "Hardware Encoder Probe Results:", Location = new Point(20, 180), AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        _lstProbeResults = new ListView
        {
            Location = new Point(20, 205),
            Size = new Size(540, 180),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lstProbeResults.Columns.Add("Profile", 110);
        _lstProbeResults.Columns.Add("Status", 80);
        _lstProbeResults.Columns.Add("Duration", 70);
        _lstProbeResults.Columns.Add("Details / Diagnostics", 260);

        tabVideo.Controls.AddRange([lblFps, _cmbFrameRate, lblQuality, _cmbQuality, lblEncoder, _cmbEncoder, _btnRedetectEncoder, _chkCaptureCursor, _chkDownscale, lblProbeHeader, _lstProbeResults]);

        // 3. Audio Tab
        var tabAudio = new TabPage("Audio");
        var lblMic = new Label { Text = "Microphone mode:", Location = new Point(20, 22), AutoSize = true };
        _cmbMicMode = new ComboBox { Location = new Point(160, 18), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbMicMode.Items.AddRange(["DefaultCommunications (Headset/Hands-Free)", "DefaultMultimedia", "None"]);

        var lblOut = new Label { Text = "System audio mode:", Location = new Point(20, 62), AutoSize = true };
        _cmbOutputMode = new ComboBox { Location = new Point(160, 58), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbOutputMode.Items.AddRange(["DefaultPlusCommunications (All meeting audio)", "Default only", "None"]);

        var lblMicG = new Label { Text = "Mic gain:", Location = new Point(20, 105), AutoSize = true };
        _trkMicGain = new TrackBar { Minimum = -20, Maximum = 20, Location = new Point(150, 100), Width = 180, TickFrequency = 5 };
        _lblMicGainVal = new Label { Text = "0 dB", Location = new Point(340, 105), AutoSize = true };
        _trkMicGain.ValueChanged += (_, _) => _lblMicGainVal.Text = $"{_trkMicGain.Value} dB";

        var lblSysG = new Label { Text = "System audio gain:", Location = new Point(20, 150), AutoSize = true };
        _trkSysGain = new TrackBar { Minimum = -20, Maximum = 20, Location = new Point(150, 145), Width = 180, TickFrequency = 5 };
        _lblSysGainVal = new Label { Text = "0 dB", Location = new Point(340, 150), AutoSize = true };
        _trkSysGain.ValueChanged += (_, _) => _lblSysGainVal.Text = $"{_trkSysGain.Value} dB";

        var lblJitter = new Label { Text = "Jitter target (ms):", Location = new Point(20, 195), AutoSize = true };
        _numJitterBuffer = new NumericUpDown { Minimum = 20, Maximum = 500, Value = 100, Location = new Point(160, 192), Width = 70 };

        var lblAvOff = new Label { Text = "A/V offset (ms):", Location = new Point(20, 235), AutoSize = true };
        _numAvOffset = new NumericUpDown { Minimum = -500, Maximum = 500, Value = 0, Location = new Point(160, 232), Width = 70 };

        tabAudio.Controls.AddRange([lblMic, _cmbMicMode, lblOut, _cmbOutputMode, lblMicG, _trkMicGain, _lblMicGainVal, lblSysG, _trkSysGain, _lblSysGainVal, lblJitter, _numJitterBuffer, lblAvOff, _numAvOffset]);

        // 4. Storage Tab
        var tabStorage = new TabPage("Storage") { Padding = new Padding(12) };
        var lblLocs = new Label { Text = "Storage Locations (in priority order):", Location = new Point(16, 12), AutoSize = true };
        _lstLocations = new ListBox { Location = new Point(16, 32), Size = new Size(380, 95) };
        _btnAddLocation = new Button { Text = "Add…", Location = new Point(406, 32), Width = 80, Height = 28 };
        _btnAddLocation.Click += (_, _) => AddStorageLocation();
        _btnRemoveLocation = new Button { Text = "Remove", Location = new Point(406, 66), Width = 80, Height = 28 };
        _btnRemoveLocation.Click += (_, _) => RemoveStorageLocation();

        var lblSplit = new Label { Text = "Split file every:", Location = new Point(16, 138), AutoSize = true };
        _cmbSplitMinutes = new ComboBox { Location = new Point(150, 134), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbSplitMinutes.Items.AddRange(["5 minutes", "10 minutes (Default)", "15 minutes", "30 minutes", "60 minutes"]);

        var lblFormat = new Label { Text = "Final container:", Location = new Point(16, 170), AutoSize = true };
        _cmbOutputFormat = new ComboBox { Location = new Point(150, 166), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbOutputFormat.Items.AddRange(["MKV (Recommended)", "MP4 (Compatible)", "TS (Raw live format)"]);

        _chkKeepTs = new CheckBox { Text = "Keep raw .ts files after remuxing", Location = new Point(16, 202), AutoSize = true };
        _chkFailback = new CheckBox { Text = "Return to primary location when space becomes available (10 GB buffer)", Location = new Point(16, 226), AutoSize = true };

        _chkRetention = new CheckBox { Text = "Delete recordings older than:", Location = new Point(16, 252), AutoSize = true };
        _numRetentionDays = new NumericUpDown { Minimum = 1, Maximum = 365, Value = 30, Location = new Point(220, 250), Width = 60 };

        _chkShowSavedDialog = new CheckBox { Text = "Show 'Recording saved' dialog when recording stops", Location = new Point(16, 282), AutoSize = true };
        _chkMergeOnSave = new CheckBox { Text = "Merge multi-part sessions into single file on save", Location = new Point(16, 308), AutoSize = true };
        _chkDeletePartsAfterMerge = new CheckBox { Text = "Delete individual parts after successful merge", Location = new Point(36, 334), AutoSize = true };

        tabStorage.Controls.AddRange([lblLocs, _lstLocations, _btnAddLocation, _btnRemoveLocation, lblSplit, _cmbSplitMinutes, lblFormat, _cmbOutputFormat, _chkKeepTs, _chkFailback, _chkRetention, _numRetentionDays, _chkShowSavedDialog, _chkMergeOnSave, _chkDeletePartsAfterMerge]);

        // 5. Hotkeys Tab
        var tabHotkeys = new TabPage("Hotkeys") { Padding = new Padding(12) };
        var lblHkR = new Label { Text = "Start / Stop && Save:", Location = new Point(20, 25), AutoSize = true, UseMnemonic = false };
        _txtHkStartStop = new TextBox { Location = new Point(160, 22), Width = 180, ReadOnly = true };

        var lblHkX = new Label { Text = "Mute mic in recording:", Location = new Point(20, 65), AutoSize = true, UseMnemonic = false };
        _txtHkMuteMic = new TextBox { Location = new Point(160, 62), Width = 180, ReadOnly = true };

        var lblHkM = new Label { Text = "Add marker:", Location = new Point(20, 105), AutoSize = true, UseMnemonic = false };
        _txtHkMarker = new TextBox { Location = new Point(160, 102), Width = 180, ReadOnly = true };

        var lblHkP = new Label { Text = "Pause/Resume:", Location = new Point(20, 145), AutoSize = true, UseMnemonic = false };
        _txtHkPause = new TextBox { Location = new Point(160, 142), Width = 180, ReadOnly = true };

        var lblHkS = new Label { Text = "Show Status:", Location = new Point(20, 185), AutoSize = true, UseMnemonic = false };
        _txtHkStatus = new TextBox { Location = new Point(160, 182), Width = 180, ReadOnly = true };

        _btnResetHotkeys = new Button { Text = "Reset to Defaults", Location = new Point(160, 225), Width = 130, Height = 28, UseMnemonic = false };
        _btnResetHotkeys.Click += (_, _) =>
        {
            _txtHkStartStop.Text = "Ctrl+Alt+Shift+R";
            _txtHkMuteMic.Text = "Ctrl+Alt+Shift+X";
            _txtHkMarker.Text = "Ctrl+Alt+Shift+M";
            _txtHkPause.Text = "Ctrl+Alt+Shift+P";
            _txtHkStatus.Text = "Ctrl+Alt+Shift+S";
        };

        tabHotkeys.Controls.AddRange([lblHkR, _txtHkStartStop, lblHkX, _txtHkMuteMic, lblHkM, _txtHkMarker, lblHkP, _txtHkPause, lblHkS, _txtHkStatus, _btnResetHotkeys]);

        // 6. Advanced Tab
        var tabAdvanced = new TabPage("Advanced");
        var lblFfmpeg = new Label { Text = "FFmpeg executable path:", Location = new Point(20, 25), AutoSize = true };
        _txtFfmpegPath = new TextBox { Location = new Point(20, 48), Width = 380 };
        _btnBrowseFfmpeg = new Button { Text = "Browse…", Location = new Point(410, 46), Width = 75, Height = 26, UseMnemonic = false };
        _btnBrowseFfmpeg.Click += (_, _) => BrowseFfmpeg();

        var lblLog = new Label { Text = "Log level:", Location = new Point(20, 95), AutoSize = true };
        _cmbLogLevel = new ComboBox { Location = new Point(100, 92), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbLogLevel.Items.AddRange(["Debug", "Information", "Warning", "Error"]);

        _btnOpenLogs = new Button { Text = "Open Logs Folder", Location = new Point(20, 140), Width = 150, Height = 28, UseMnemonic = false };
        _btnOpenLogs.Click += (_, _) => OpenLogsFolder();

        _btnExportDiagnostics = new Button { Text = "Export Diagnostics…", Location = new Point(185, 140), Width = 150, Height = 28, UseMnemonic = false };
        _btnExportDiagnostics.Click += (_, _) => ExportDiagnostics();

        _btnRerunWizard = new Button { Text = "Re-run Setup Wizard…", Location = new Point(20, 185), Width = 150, Height = 28, UseMnemonic = false };
        _btnRerunWizard.Click += (_, _) => RerunWizard();

        _btnResetDefaults = new Button { Text = "Reset All to Defaults", Location = new Point(185, 185), Width = 150, Height = 28, UseMnemonic = false };
        _btnResetDefaults.Click += (_, _) => ResetAllDefaults();

        // About Box (Section 5.7)
        var aboutInfo = GetAboutInfo();
        var grpAbout = new GroupBox
        {
            Text = "About ScreenVault",
            Location = new Point(20, 225),
            Size = new Size(540, 160)
        };

        var lblAboutVersion = new Label { Text = $"Version: {aboutInfo.Version}", Location = new Point(16, 26), AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        var lblAboutScope = new Label { Text = $"Install Scope: {aboutInfo.Scope}", Location = new Point(16, 52), AutoSize = true };
        var lblAboutPath = new Label { Text = $"Install Path: {aboutInfo.InstallPath}", Location = new Point(16, 78), AutoSize = true };
        var lblAboutFfmpeg = new Label { Text = $"FFmpeg: {aboutInfo.FfmpegVersion}", Location = new Point(16, 104), AutoSize = true };

        grpAbout.Controls.AddRange([lblAboutVersion, lblAboutScope, lblAboutPath, lblAboutFfmpeg]);

        tabAdvanced.Controls.AddRange([lblFfmpeg, _txtFfmpegPath, _btnBrowseFfmpeg, lblLog, _cmbLogLevel, _btnOpenLogs, _btnExportDiagnostics, _btnRerunWizard, _btnResetDefaults, grpAbout]);

        tabControl.TabPages.AddRange([tabGeneral, tabVideo, tabAudio, tabStorage, tabHotkeys, tabAdvanced]);

        // Bottom buttons
        _lblValidation = new Label
        {
            Location = new Point(18, 516),
            Size = new Size(260, 20),
            ForeColor = Color.Red,
            Text = string.Empty,
            UseMnemonic = false
        };

        _btnOk = new Button { Text = "OK", Location = new Point(310, 510), Width = 75, Height = 28, UseMnemonic = false };
        _btnOk.Click += (_, _) => { if (SaveWorkingCopy()) { DialogResult = DialogResult.OK; Close(); } };

        _btnCancel = new Button { Text = "Cancel", Location = new Point(395, 510), Width = 75, Height = 28, DialogResult = DialogResult.Cancel, UseMnemonic = false };
        _btnApply = new Button { Text = "Apply", Location = new Point(480, 510), Width = 75, Height = 28, UseMnemonic = false };
        _btnApply.Click += (_, _) => SaveWorkingCopy();

        Controls.Add(tabControl);
        Controls.Add(_lblValidation);
        Controls.Add(_btnOk);
        Controls.Add(_btnCancel);
        Controls.Add(_btnApply);

        tabControl.SelectedIndexChanged += (_, _) =>
        {
            _audioEngine?.SetMonitoring(tabControl.SelectedTab == tabAudio);
        };

        FormClosing += (_, _) =>
        {
            _audioEngine?.SetMonitoring(false);
        };

        LoadSettingsIntoUi();
    }

    private void LoadSettingsIntoUi()
    {
        // General
        _chkStartWithWindows.Checked = _workingCopy.General.StartWithWindows;
        _chkAutoStartRecording.Checked = _workingCopy.General.StartRecordingOnLaunch;
        _chkStartMinimized.Checked = _workingCopy.General.MinimizeToTrayOnLaunch;
        _numStartupDelay.Value = _workingCopy.General.StartupDelaySeconds;
        _chkConfirmStop.Checked = _workingCopy.General.ConfirmBeforeStop;
        _chkNotifyDevice.Checked = _workingCopy.General.Notifications.DeviceSwitch;
        _chkNotifyStorage.Checked = _workingCopy.General.Notifications.Storage;

        // Video
        _cmbFrameRate.SelectedIndex = _workingCopy.Video.FrameRate switch
        {
            24 => 1,
            30 => 2,
            _ => 0
        };

        _cmbQuality.SelectedIndex = _workingCopy.Video.Quality switch
        {
            VideoQuality.Small => 0,
            VideoQuality.High => 2,
            _ => 1
        };

        _cmbEncoder.SelectedItem = _workingCopy.Video.Encoder;
        if (_cmbEncoder.SelectedIndex < 0) _cmbEncoder.SelectedIndex = 0;

        _chkCaptureCursor.Checked = _workingCopy.Video.CaptureCursor;
        _chkDownscale.Checked = _workingCopy.Video.DownscaleTo1080p;

        // Audio
        _cmbMicMode.SelectedIndex = _workingCopy.Audio.MicMode switch
        {
            MicMode.DefaultMultimedia => 1,
            MicMode.None => 2,
            _ => 0
        };
        _cmbOutputMode.SelectedIndex = _workingCopy.Audio.OutputMode switch
        {
            OutputMode.Default => 1,
            OutputMode.None => 2,
            _ => 0
        };
        _trkMicGain.Value = (int)Math.Clamp(_workingCopy.Audio.MicGainDb, -20, 20);
        _lblMicGainVal.Text = $"{_trkMicGain.Value} dB";
        _trkSysGain.Value = (int)Math.Clamp(_workingCopy.Audio.SystemGainDb, -20, 20);
        _lblSysGainVal.Text = $"{_trkSysGain.Value} dB";
        _numJitterBuffer.Value = Math.Clamp(_workingCopy.Audio.JitterTargetMs, 20, 500);
        _numAvOffset.Value = Math.Clamp(_workingCopy.Audio.AvOffsetMs, -500, 500);

        // Storage
        _lstLocations.Items.Clear();
        foreach (var loc in _workingCopy.Storage.Locations)
        {
            var expanded = Environment.ExpandEnvironmentVariables(loc.Path);
            _lstLocations.Items.Add($"{expanded} (min free: {loc.MinFreeGb} GB)");
        }

        _cmbSplitMinutes.SelectedIndex = _workingCopy.Storage.SplitMinutes switch
        {
            5 => 0,
            15 => 2,
            30 => 3,
            60 => 4,
            _ => 1
        };

        _cmbOutputFormat.SelectedIndex = _workingCopy.Storage.OutputFormat switch
        {
            OutputContainerFormat.Mp4 => 1,
            OutputContainerFormat.Ts => 2,
            _ => 0
        };

        _chkKeepTs.Checked = _workingCopy.Storage.KeepTsAfterRemux;
        _chkFailback.Checked = _workingCopy.Storage.FailbackToPrimary;
        _chkRetention.Checked = _workingCopy.Storage.Retention.Enabled;
        _numRetentionDays.Value = _workingCopy.Storage.Retention.KeepDays;

        // Saving
        _chkShowSavedDialog.Checked = _workingCopy.Saving.ShowSavedDialog;
        _chkMergeOnSave.Checked = _workingCopy.Saving.MergeOnSave;
        _chkDeletePartsAfterMerge.Checked = _workingCopy.Saving.DeletePartsAfterMerge;

        // Hotkeys
        _txtHkStartStop.Text = _workingCopy.Hotkeys.StartStop;
        _txtHkMuteMic.Text = _workingCopy.Hotkeys.MuteMic;
        _txtHkMarker.Text = _workingCopy.Hotkeys.AddMarker;
        _txtHkPause.Text = _workingCopy.Hotkeys.PauseResume;
        _txtHkStatus.Text = _workingCopy.Hotkeys.ShowStatus;

        // Advanced
        _txtFfmpegPath.Text = _workingCopy.Advanced.FfmpegPath ?? string.Empty;
        _cmbLogLevel.SelectedItem = _workingCopy.Advanced.LogLevel;
        if (_cmbLogLevel.SelectedIndex < 0) _cmbLogLevel.SelectedIndex = 1;

        // Video probe details
        RefreshProbeDetails(EncoderProbe.LastResult?.Details);
    }

    private void RefreshProbeDetails(IReadOnlyList<ProfileProbeStatus>? details)
    {
        _lstProbeResults.Items.Clear();
        if (details == null || details.Count == 0) return;

        foreach (var status in details)
        {
            var item = new ListViewItem(status.ProfileName);
            item.SubItems.Add(status.Success ? "✔ Passed" : "✖ Failed");
            item.SubItems.Add($"{status.Duration.TotalMilliseconds:F0} ms");
            item.SubItems.Add(status.Reason);
            _lstProbeResults.Items.Add(item);
        }
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

        _workingCopy.Video.FrameRate = _cmbFrameRate.SelectedIndex switch
        {
            1 => 24,
            2 => 30,
            _ => 15
        };

        _workingCopy.Video.Quality = _cmbQuality.SelectedIndex switch
        {
            0 => VideoQuality.Small,
            2 => VideoQuality.High,
            _ => VideoQuality.Balanced
        };

        _workingCopy.Video.Encoder = _cmbEncoder.SelectedItem?.ToString() ?? "Auto";
        _workingCopy.Video.CaptureCursor = _chkCaptureCursor.Checked;
        _workingCopy.Video.DownscaleTo1080p = _chkDownscale.Checked;

        _workingCopy.Audio.MicMode = _cmbMicMode.SelectedIndex switch
        {
            1 => MicMode.DefaultMultimedia,
            2 => MicMode.None,
            _ => MicMode.DefaultCommunications
        };

        _workingCopy.Audio.OutputMode = _cmbOutputMode.SelectedIndex switch
        {
            1 => OutputMode.Default,
            2 => OutputMode.None,
            _ => OutputMode.DefaultPlusCommunications
        };

        _workingCopy.Audio.MicGainDb = _trkMicGain.Value;
        _workingCopy.Audio.SystemGainDb = _trkSysGain.Value;
        _workingCopy.Audio.JitterTargetMs = (int)_numJitterBuffer.Value;
        _workingCopy.Audio.AvOffsetMs = (int)_numAvOffset.Value;

        _workingCopy.Storage.SplitMinutes = _cmbSplitMinutes.SelectedIndex switch
        {
            0 => 5,
            2 => 15,
            3 => 30,
            4 => 60,
            _ => 10
        };

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
        _workingCopy.Hotkeys.StartStop = _txtHkStartStop.Text;
        _workingCopy.Hotkeys.MuteMic = _txtHkMuteMic.Text;
        _workingCopy.Hotkeys.AddMarker = _txtHkMarker.Text;
        _workingCopy.Hotkeys.PauseResume = _txtHkPause.Text;
        _workingCopy.Hotkeys.ShowStatus = _txtHkStatus.Text;

        _workingCopy.Advanced.FfmpegPath = string.IsNullOrWhiteSpace(_txtFfmpegPath.Text) ? null : _txtFfmpegPath.Text.Trim();
        _workingCopy.Advanced.LogLevel = _cmbLogLevel.SelectedItem?.ToString() ?? "Information";

        var validationResult = SettingsValidator.Validate(_workingCopy);
        if (!validationResult.IsValid)
        {
            _lblValidation.Text = string.Join("; ", validationResult.Errors);
            return false;
        }

        _lblValidation.Text = string.Empty;
        _settingsService.Save(_workingCopy);

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
        _btnRedetectEncoder.Text = "Probing…";
        try
        {
            var paths = new FfmpegLocator().Locate();
            var result = await EncoderProbe.ProbeAsync(paths.FfmpegPath).ConfigureAwait(true);
            _workingCopy.Video.DetectedEncoderProfile = result.ProfileName;
            _workingCopy.Video.EncoderFingerprint = result.Fingerprint;
            RefreshProbeDetails(result.Details);
            MessageBox.Show(this, $"Detected best hardware encoder: {result.ProfileName}", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Encoder probe failed: {ex.Message}", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnRedetectEncoder.Text = "Re-detect";
            _btnRedetectEncoder.Enabled = true;
        }
    }

    private void AddStorageLocation()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a storage folder for ScreenVault recordings" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var newEntry = new StorageLocationConfig { Path = dlg.SelectedPath, MinFreeGb = 5, Enabled = true };
            _workingCopy.Storage.Locations.Add(newEntry);
            _lstLocations.Items.Add($"{newEntry.Path} (min free: {newEntry.MinFreeGb} GB)");
        }
    }

    private void RemoveStorageLocation()
    {
        var idx = _lstLocations.SelectedIndex;
        if (idx >= 0 && idx < _workingCopy.Storage.Locations.Count)
        {
            if (_workingCopy.Storage.Locations.Count <= 1)
            {
                MessageBox.Show(this, "ScreenVault requires at least one storage location.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _workingCopy.Storage.Locations.RemoveAt(idx);
            _lstLocations.Items.RemoveAt(idx);
        }
    }

    private void BrowseFfmpeg()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "ffmpeg.exe|ffmpeg.exe|All files (*.*)|*.*",
            Title = "Locate bundled or custom ffmpeg.exe"
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
            MessageBox.Show(this, "Audio engine is not available.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var wizard = new FirstRunWizardForm(_settingsService, _audioEngine);
        if (wizard.ShowDialog(this) == DialogResult.OK)
        {
            _workingCopy = CloneSettings(_settingsService.Current);
            LoadSettingsIntoUi();
        }
    }

    private void ExportDiagnostics()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
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

                MessageBox.Show(this, $"Diagnostics exported successfully to your Desktop:\n{Path.GetFileName(zipPath)}", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            MessageBox.Show(this, "Failed to export diagnostics: " + ex.Message, "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetAllDefaults()
    {
        if (MessageBox.Show(this, "Reset all settings to default values?", "ScreenVault", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _workingCopy = AppSettings.CreateDefault();
            LoadSettingsIntoUi();
        }
    }

    private static AppSettings CloneSettings(AppSettings source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? AppSettings.CreateDefault();
    }

    private static (string Version, string Scope, string InstallPath, string FfmpegVersion) GetAboutInfo()
    {
        var version = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "1.2.0";
        var installPath = AppContext.BaseDirectory.TrimEnd('\\');

        string scope = "Per-User";
        if (StartWithWindows.HasHklmRunEntry() ||
            installPath.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
        {
            scope = "All Users (Machine)";
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

        string ffmpegVersion = "Unknown";
        var ffmpegVersionPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "VERSION.txt");
        if (File.Exists(ffmpegVersionPath))
        {
            try
            {
                var lines = File.ReadAllLines(ffmpegVersionPath);
                var firstLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
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
        else
        {
            ffmpegVersion = "ffmpeg (bundled)";
        }

        return (version, scope, installPath, ffmpegVersion);
    }
}

