using System.Diagnostics;
using System.Globalization;
using System.Media;
using ScreenVault.App.Platform;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

/// <summary>Five-step setup wizard: welcome, storage, audio check, startup &amp; clean-up, summary.</summary>
public sealed class FirstRunWizardForm : ModernForm
{
    private const int TotalSteps = 5;
    private const int ContentWidth = 468;

    private static readonly string[] StepTitles = ["Welcome", "Storage", "Audio check", "Startup & clean-up", "Finish"];

    private readonly ISettingsService _settingsService;
    private readonly IAudioEngine _audioEngine;
    private readonly StepList _steps;
    private readonly SurfacePanel[] _pages = new SurfacePanel[TotalSteps];
    private readonly ModernButton _btnBack;
    private readonly ModernButton _btnNext;
    private readonly ModernButton _btnCancel;
    private readonly System.Windows.Forms.Timer _vuTimer;
    private int _currentStep = 1;

    // Step 2
    private readonly TextField _txtPrimaryStorage = new();
    private readonly TextField _txtBackupStorage = new();
    private readonly ModernProgressBar _barPrimary = new();
    private readonly ModernProgressBar _barBackup = new();
    private readonly TextLabel _lblPrimaryFree = new(string.Empty, Typography.Caption, TextTone.Secondary);
    private readonly TextLabel _lblBackupFree = new(string.Empty, Typography.Caption, TextTone.Secondary);
    private readonly TextLabel _lblStorageWarning = new(string.Empty, Typography.Caption, TextTone.Warning, wrap: true);

    // Step 3
    private readonly VuMeterControl _vuMic = new() { AccessibleName = "Microphone level" };
    private readonly VuMeterControl _vuOut = new() { AccessibleName = "System audio level" };
    private readonly StatusPill _pillMic = new() { Text = "Listening…", Tone = Tone.Neutral };
    private readonly StatusPill _pillOut = new() { Text = "Waiting…", Tone = Tone.Neutral };
    private readonly CardPanel _privacyWarning = new() { AccentTone = Tone.Warning, ManualLayout = true, Visible = false };
    private bool _micHeard;
    private bool _outHeard;

    // Step 4
    private readonly ToggleSwitch _chkStartWithWindows = new();
    private readonly ToggleSwitch _chkAutoStart = new();
    private readonly ToggleSwitch _chkRetention = new();
    private readonly NumberField _numRetentionDays = new() { Minimum = 1, Maximum = 365, Suffix = "days", Width = 130 };
    private readonly CardPanel _startupCard = new() { Dividers = true, Padding = new Padding(0), Spacing = 0 };

    // Step 5
    private readonly CardPanel _shortcutsCard = new() { Dividers = true, Padding = new Padding(0), Spacing = 0 };

    public FirstRunWizardForm(ISettingsService settingsService, IAudioEngine audioEngine)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));

        Text = "ScreenVault Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 560);

        // ── Left rail ────────────────────────────────────────────────────────────────
        var rail = new SurfacePanel { Surface = SurfaceKind.Sidebar, Size = new Size(240, 560), Dock = DockStyle.Left };
        var appIcon = AppIcon.Get();
        if (appIcon != null)
        {
            using var large = new Icon(appIcon, 48, 48);
            rail.Controls.Add(new PictureBox { Image = large.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(28, 30, 44, 44) });
        }

        rail.Controls.Add(new TextLabel("Set up ScreenVault", Typography.Title) { Location = new Point(26, 88) });
        _steps = new StepList(StepTitles) { Bounds = new Rectangle(18, 136, 204, 240) };
        rail.Controls.Add(_steps);
        rail.Controls.Add(new TextLabel("You can change all of this later in Settings.", Typography.Caption, TextTone.Tertiary, wrap: true)
        {
            Bounds = new Rectangle(26, 488, 190, 40)
        });

        // ── Footer ───────────────────────────────────────────────────────────────────
        var footer = new SurfacePanel { Size = new Size(540, 68), Dock = DockStyle.Bottom, TopDivider = true };
        _btnCancel = new ModernButton("Cancel", ButtonKind.Subtle) { Bounds = new Rectangle(28, 18, 90, 32), DialogResult = DialogResult.Cancel };
        _btnBack = new ModernButton("Back", ButtonKind.Secondary) { Bounds = new Rectangle(292, 18, 100, 32), Enabled = false };
        _btnBack.Click += (_, _) => NavigateStep(-1);
        _btnNext = new ModernButton("Next", ButtonKind.Primary, Glyphs.ChevronRight) { Bounds = new Rectangle(400, 18, 112, 32) };
        _btnNext.Click += (_, _) => NavigateStep(1);
        footer.Controls.AddRange([_btnCancel, _btnBack, _btnNext]);
        CancelButton = _btnCancel;

        // ── Pages ────────────────────────────────────────────────────────────────────
        var host = new SurfacePanel { Size = new Size(540, 492), Dock = DockStyle.Fill };
        _pages[0] = BuildWelcomePage();
        _pages[1] = BuildStoragePage();
        _pages[2] = BuildAudioPage();
        _pages[3] = BuildStartupPage();
        _pages[4] = BuildFinishPage();
        foreach (var page in _pages)
        {
            page.Size = new Size(540, 492);
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            host.Controls.Add(page);
        }

        Controls.Add(host);
        Controls.Add(footer);
        Controls.Add(rail);

        _vuTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _vuTimer.Tick += (_, _) => UpdateAudioCheck();

        ResumeLayout(false);
        PerformLayout();

        ShowStep(1);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopVuTimer();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _vuTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    // ── Pages ────────────────────────────────────────────────────────────────────────

    private static SurfacePanel NewPage(string title, string subtitle)
    {
        var page = new SurfacePanel { Size = new Size(540, 492) };
        page.Controls.Add(new TextLabel(title, Typography.Display) { Location = new Point(34, 30) });
        page.Controls.Add(new TextLabel(subtitle, Typography.Body, TextTone.Secondary, wrap: true) { Bounds = new Rectangle(36, 74, ContentWidth, 44) });
        return page;
    }

    private static SurfacePanel BuildWelcomePage()
    {
        var page = NewPage("Welcome to ScreenVault", "An always-on recorder for your screen, microphone and meeting audio that keeps going when devices change, apps crash or a drive fills up.");
        page.Controls.Add(FeatureTile(Glyphs.Shield, Tone.Success, "Crash-proof by design", "Video reaches the disk every second. Even a power cut costs only a moment.", 140));
        page.Controls.Add(FeatureTile(Glyphs.Record, Tone.Danger, "Always visible", "A red dot in the notification area shows when recording is on. There is no hidden mode.", 222));
        page.Controls.Add(FeatureTile(Glyphs.Lock, Tone.Accent, "100% offline", "No accounts, uploads or telemetry. Recordings never leave your drives.", 304));
        return page;
    }

    private static CardPanel FeatureTile(char glyph, Tone tone, string title, string description, int top)
    {
        var tile = new CardPanel { ManualLayout = true, Bounds = new Rectangle(36, top, ContentWidth, 72) };
        tile.Controls.Add(new GlyphBadge { Glyph = glyph, Tone = tone, Bounds = new Rectangle(16, 16, 40, 40) });
        tile.Controls.Add(new TextLabel(title, Typography.BodyStrong) { Location = new Point(70, 15) });
        tile.Controls.Add(new TextLabel(description, Typography.Caption, TextTone.Secondary, wrap: true) { Bounds = new Rectangle(70, 36, ContentWidth - 86, 32) });
        return tile;
    }

    private SurfacePanel BuildStoragePage()
    {
        var page = NewPage("Where should recordings go?", "If the first drive runs low, ScreenVault continues on the backup drive automatically — without a gap in the recording.");

        var locations = _settingsService.Current.Storage.Locations;
        _txtPrimaryStorage.Text = locations.Count > 0 ? locations[0].Path : @"%USERPROFILE%\Videos\Screen Recordings";
        _txtBackupStorage.Text = locations.Count > 1 ? locations[1].Path : @"D:\ScreenVault Backup";
        _txtBackupStorage.PlaceholderText = "Optional — a folder on another drive";

        AddLocationEditor(page, "Primary folder", _txtPrimaryStorage, _barPrimary, _lblPrimaryFree, 136);
        AddLocationEditor(page, "Backup folder (on a different drive)", _txtBackupStorage, _barBackup, _lblBackupFree, 250);
        _lblStorageWarning.Bounds = new Rectangle(36, 364, ContentWidth, 40);
        page.Controls.Add(_lblStorageWarning);

        _txtPrimaryStorage.TextChanged += (_, _) => UpdateStorageInfo();
        _txtBackupStorage.TextChanged += (_, _) => UpdateStorageInfo();
        UpdateStorageInfo();
        return page;
    }

    private void AddLocationEditor(Control page, string label, TextField field, ModernProgressBar bar, TextLabel freeLabel, int top)
    {
        page.Controls.Add(new TextLabel(label, Typography.BodyStrong) { Location = new Point(36, top) });
        field.Bounds = new Rectangle(36, top + 26, 356, 32);
        field.LeadingGlyph = Glyphs.Folder;
        var browse = new ModernButton("Browse…", ButtonKind.Secondary) { Bounds = new Rectangle(400, top + 26, 104, 32) };
        browse.Click += (_, _) => BrowseFolder(field);
        bar.Bounds = new Rectangle(36, top + 68, ContentWidth, 6);
        freeLabel.Location = new Point(34, top + 80);
        page.Controls.AddRange([field, browse, bar, freeLabel]);
    }

    private SurfacePanel BuildAudioPage()
    {
        var page = NewPage("Check your audio", "Say something and play the test sound. Both meters should move — that's how you know meetings will be recorded with sound.");

        page.Controls.Add(AudioCard(Glyphs.Microphone, "Microphone", "Say something — the bar should move.", _pillMic, _vuMic, 132));
        page.Controls.Add(AudioCard(Glyphs.Volume, "System audio", "Play the test sound through your speakers or headset.", _pillOut, _vuOut, 240));

        var btnChime = new ModernButton("Play test sound", ButtonKind.Secondary, Glyphs.Play) { Bounds = new Rectangle(36, 350, 150, 32) };
        btnChime.Click += (_, _) =>
        {
            try
            {
                SystemSounds.Asterisk.Play();
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                Log.Debug(ex, "Could not play the test chime.");
            }
        };

        var btnPrivacy = new ModernButton("Microphone privacy settings", ButtonKind.Subtle, Glyphs.Shield) { Bounds = new Rectangle(194, 350, 230, 32) };
        btnPrivacy.Click += (_, _) => OpenMicPrivacySettings();

        _privacyWarning.Bounds = new Rectangle(36, 396, ContentWidth, 56);
        _privacyWarning.Controls.Add(new GlyphIcon { Glyph = Glyphs.Warning, Tone = TextTone.Warning, Bounds = new Rectangle(12, 18, 20, 20) });
        _privacyWarning.Controls.Add(new TextLabel("Windows is blocking microphone access for desktop apps. Turn on \"Let desktop apps access your microphone\" in privacy settings.", Typography.Caption, TextTone.Warning, wrap: true)
        {
            Bounds = new Rectangle(40, 9, ContentWidth - 52, 40)
        });

        page.Controls.AddRange([btnChime, btnPrivacy, _privacyWarning]);
        return page;
    }

    private static CardPanel AudioCard(char glyph, string title, string description, StatusPill pill, VuMeterControl meter, int top)
    {
        var card = new CardPanel { ManualLayout = true, Bounds = new Rectangle(36, top, ContentWidth, 96) };
        card.Controls.Add(new GlyphBadge { Glyph = glyph, Tone = Tone.Accent, Bounds = new Rectangle(16, 16, 36, 36) });
        card.Controls.Add(new TextLabel(title, Typography.BodyStrong) { Location = new Point(64, 15) });
        card.Controls.Add(new TextLabel(description, Typography.Caption, TextTone.Secondary) { Location = new Point(64, 36) });
        pill.Location = new Point(ContentWidth - 16 - 96, 16);
        pill.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        card.Controls.Add(pill);
        meter.Bounds = new Rectangle(64, 62, ContentWidth - 80, 20);
        card.Controls.Add(meter);
        return card;
    }

    private SurfacePanel BuildStartupPage()
    {
        var page = NewPage("Startup and clean-up", "Choose how ScreenVault starts and whether old recordings are removed to save space.");
        var current = _settingsService.Current;
        _chkStartWithWindows.Checked = current.General.StartWithWindows;
        _chkAutoStart.Checked = current.General.StartRecordingOnLaunch;
        _chkRetention.Checked = current.Storage.Retention.Enabled;
        _numRetentionDays.Value = Math.Clamp(current.Storage.Retention.KeepDays, 1, 365);
        _numRetentionDays.Enabled = _chkRetention.Checked;
        _chkRetention.CheckedChanged += (_, _) => _numRetentionDays.Enabled = _chkRetention.Checked;

        _startupCard.Bounds = new Rectangle(36, 132, ContentWidth, 260);
        _startupCard.Controls.Add(new SettingRow("Start with Windows", "Open ScreenVault in the notification area when you sign in (recommended).", _chkStartWithWindows, Glyphs.Monitor));
        _startupCard.Controls.Add(new SettingRow("Start recording automatically", "Begin recording as soon as ScreenVault starts.", _chkAutoStart, Glyphs.Record));
        _startupCard.Controls.Add(new SettingRow("Delete old recordings", "Off by default. Protected recordings are never deleted.", _chkRetention, Glyphs.Delete));
        _startupCard.Controls.Add(new SettingRow("Keep recordings for", null, _numRetentionDays));
        page.Controls.Add(_startupCard);
        return page;
    }

    private SurfacePanel BuildFinishPage()
    {
        var page = new SurfacePanel { Size = new Size(540, 492) };
        page.Controls.Add(new GlyphBadge { Glyph = Glyphs.CheckMark, Tone = Tone.Success, Filled = true, Bounds = new Rectangle(36, 34, 52, 52) });
        page.Controls.Add(new TextLabel("You're all set", Typography.Display) { Location = new Point(34, 100) });
        page.Controls.Add(new TextLabel("Click Finish to save. ScreenVault lives in the notification area next to the clock — click its icon at any time to see what's happening.", Typography.Body, TextTone.Secondary, wrap: true)
        {
            Bounds = new Rectangle(36, 144, ContentWidth, 44)
        });
        page.Controls.Add(new TextLabel("Handy shortcuts", Typography.BodyStrong) { Location = new Point(36, 204) });

        var hotkeys = _settingsService.Current.Hotkeys;
        _shortcutsCard.Bounds = new Rectangle(36, 230, ContentWidth, 224);
        _shortcutsCard.Controls.Add(ShortcutRow("Start / stop & save", hotkeys.StartStop, Glyphs.Record));
        _shortcutsCard.Controls.Add(ShortcutRow("Add a marker", hotkeys.AddMarker, Glyphs.Flag));
        _shortcutsCard.Controls.Add(ShortcutRow("Pause / resume", hotkeys.PauseResume, Glyphs.Pause));
        _shortcutsCard.Controls.Add(ShortcutRow("Show the status window", hotkeys.ShowStatus, Glyphs.Monitor));
        page.Controls.Add(_shortcutsCard);
        return page;

        static SettingRow ShortcutRow(string title, string hotkey, char glyph) =>
            new(title, null, new HotkeyField { ReadOnly = true, Hotkey = hotkey, Size = new Size(230, 28) }, glyph);
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────

    private void NavigateStep(int delta)
    {
        var target = _currentStep + delta;
        if (target < 1)
        {
            return;
        }

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
        _steps.Current = step - 1;
        _btnBack.Enabled = step > 1;
        _btnNext.Text = step == TotalSteps ? "Finish" : "Next";
        _btnNext.Glyph = step == TotalSteps ? Glyphs.CheckMark : Glyphs.ChevronRight;

        StopVuTimer();
        for (var i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visible = i == step - 1;
        }

        switch (step)
        {
            case 3:
                _privacyWarning.Visible = !MicPrivacyChecker.IsMicrophoneAccessAllowed();
                _audioEngine.SetMonitoring(true);
                _vuTimer.Start();
                break;
            case 4:
                FitCard(_startupCard);
                break;
            case 5:
                FitCard(_shortcutsCard);
                break;
        }

        _btnNext.Focus();
    }

    private static void FitCard(CardPanel card)
    {
        var preferred = card.GetPreferredSize(new Size(card.Width, 0)).Height;
        if (preferred > 0)
        {
            card.Height = preferred;
        }
    }

    private void StopVuTimer()
    {
        _vuTimer.Stop();
        _audioEngine.SetMonitoring(false);
    }

    private void UpdateAudioCheck()
    {
        try
        {
            var status = _audioEngine.GetStatus();
            var mic = status.ActiveDevices.FirstOrDefault(d => !d.IsLoopback);
            var sys = status.ActiveDevices.FirstOrDefault(d => d.IsLoopback);
            _vuMic.SetLevels(mic?.PeakDb ?? -90f, mic?.PeakDb ?? -90f, mic?.RmsDb ?? -90f, mic?.RmsDb ?? -90f);
            _vuOut.SetLevels(sys?.PeakDb ?? -90f, sys?.PeakDb ?? -90f, sys?.RmsDb ?? -90f, sys?.RmsDb ?? -90f);

            if (!_micHeard && (mic?.PeakDb ?? -90f) > -45f)
            {
                _micHeard = true;
                _pillMic.Text = "Working";
                _pillMic.Tone = Tone.Success;
            }
            else if (!_micHeard && mic == null)
            {
                _pillMic.Text = "No microphone";
                _pillMic.Tone = Tone.Warning;
            }

            if (!_outHeard && (sys?.PeakDb ?? -90f) > -50f)
            {
                _outHeard = true;
                _pillOut.Text = "Working";
                _pillOut.Tone = Tone.Success;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Log.Debug(ex, "Audio check update failed.");
        }
    }

    private void UpdateStorageInfo()
    {
        var primary = DescribeDrive(_txtPrimaryStorage.Text, _barPrimary, _lblPrimaryFree);
        var backup = DescribeDrive(_txtBackupStorage.Text, _barBackup, _lblBackupFree);

        if (string.IsNullOrWhiteSpace(_txtBackupStorage.Text))
        {
            _lblStorageWarning.Text = "Without a backup folder, recording stops if the primary drive fills up.";
        }
        else if (primary != null && backup != null && string.Equals(primary, backup, StringComparison.OrdinalIgnoreCase))
        {
            _lblStorageWarning.Text = "Both folders are on the same drive, so the backup won't help when that drive fills up. Pick a folder on another drive if you have one.";
        }
        else
        {
            _lblStorageWarning.Text = string.Empty;
        }
    }

    /// <summary>Updates a usage bar and caption; returns the drive root or null.</summary>
    private static string? DescribeDrive(string rawPath, ModernProgressBar bar, TextLabel label)
    {
        bar.Value = 0;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            label.Text = "Not set";
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim());
            var root = Path.GetPathRoot(expanded);
            if (string.IsNullOrEmpty(root))
            {
                label.Text = "Enter a full path, like D:\\Recordings";
                return null;
            }

            if (root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                label.Text = "Network folder — may disconnect during a recording";
                label.Tone = TextTone.Warning;
                return root;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                label.Text = $"Drive {root} is not available";
                label.Tone = TextTone.Warning;
                return root;
            }

            var usedFraction = 1d - (drive.AvailableFreeSpace / (double)drive.TotalSize);
            bar.Value = (int)Math.Round(usedFraction * 100);
            bar.Tone = drive.AvailableFreeSpace < 10L * 1024 * 1024 * 1024 ? Tone.Warning : Tone.Accent;
            label.Tone = drive.AvailableFreeSpace < 10L * 1024 * 1024 * 1024 ? TextTone.Warning : TextTone.Secondary;
            label.Text = string.Create(CultureInfo.CurrentCulture, $"{StorageMeterList.FormatBytes(drive.AvailableFreeSpace)} free of {StorageMeterList.FormatBytes(drive.TotalSize)} on {root.TrimEnd('\\')}");
            return root;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            label.Text = "This path doesn't look valid";
            label.Tone = TextTone.Warning;
            return null;
        }
    }

    private void BrowseFolder(TextField target)
    {
        using var fbd = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Choose a folder for recordings" };
        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = fbd.SelectedPath;
        }
    }

    private static void OpenMicPrivacySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Debug(ex, "Could not open microphone privacy settings.");
        }
    }

    private void SaveAndFinish()
    {
        try
        {
            var settings = _settingsService.Current;

            if (!string.IsNullOrWhiteSpace(_txtPrimaryStorage.Text) && settings.Storage.Locations.Count > 0)
            {
                settings.Storage.Locations[0].Path = _txtPrimaryStorage.Text.Trim();
            }

            if (!string.IsNullOrWhiteSpace(_txtBackupStorage.Text))
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

            settings.General.StartRecordingOnLaunch = _chkAutoStart.Checked;
            settings.General.StartWithWindows = _chkStartWithWindows.Checked;
            try
            {
                StartWithWindows.SetEnabled(settings.General.StartWithWindows, settings.General.StartRecordingOnLaunch);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not update the Start with Windows registration.");
            }

            settings.Storage.Retention.Enabled = _chkRetention.Checked;
            settings.Storage.Retention.KeepDays = (int)_numRetentionDays.Value;

            _settingsService.Save(settings);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save wizard settings.");
            ModernDialog.Error(this, "Could not save settings", ex.Message);
        }
    }

    /// <summary>Vertical list of wizard steps: done (check), current (filled), upcoming (outline).</summary>
    private sealed class StepList : Control, IThemeAware
    {
        private readonly string[] _titles;
        private int _current;

        public StepList(string[] titles)
        {
            _titles = titles;
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor,
                true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Font = Typography.Body;
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Current
        {
            get => _current;
            set
            {
                _current = value;
                AccessibleName = $"Step {value + 1} of {_titles.Length}: {_titles[value]}";
                Invalidate();
            }
        }

        public void ApplyTheme() => Invalidate();

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Draw.ParentBackground(this));
            Draw.PrepareHighQuality(g);

            var scale = Draw.Scale(this);
            var rowHeight = 44 * scale;
            var circle = 26 * scale;
            for (var i = 0; i < _titles.Length; i++)
            {
                var top = i * rowHeight;
                var dot = new RectangleF(8 * scale, top + ((rowHeight - circle) / 2f), circle, circle);

                if (i < _titles.Length - 1)
                {
                    using var pen = new Pen(i < _current ? p.Accent : p.Border, Math.Max(1f, 2f * scale));
                    g.DrawLine(pen, dot.X + (circle / 2f), dot.Bottom + (3 * scale), dot.X + (circle / 2f), top + rowHeight + ((rowHeight - circle) / 2f) - (3 * scale));
                }

                var done = i < _current;
                var active = i == _current;
                if (done || active)
                {
                    Draw.FillCircle(g, p.Accent, dot);
                }
                else
                {
                    Draw.FillCircle(g, p.Sidebar, dot);
                    using var pen = new Pen(p.BorderStrong, Math.Max(1f, 1.5f * scale));
                    g.DrawEllipse(pen, dot);
                }

                var label = done && Glyphs.Available ? Glyphs.CheckMark.ToString() : (i + 1).ToString(CultureInfo.InvariantCulture);
                var labelFont = done && Glyphs.Available ? Glyphs.GetFont(8f)! : Typography.CaptionStrong;
                TextRenderer.DrawText(g, label, labelFont, Rectangle.Round(dot), done || active ? p.OnAccent : p.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

                var textRect = new Rectangle((int)(dot.Right + (12 * scale)), (int)top, Width - (int)(dot.Right + (12 * scale)), (int)rowHeight);
                TextRenderer.DrawText(g, _titles[i], active ? Typography.BodyStrong : Font, textRect, active ? p.Text : done ? p.TextSecondary : p.TextTertiary,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }
    }
}
