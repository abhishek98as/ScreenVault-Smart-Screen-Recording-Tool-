using System.Globalization;
using ScreenVault.App.Platform;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

/// <summary>
/// Windows 11 style settings: sidebar navigation, grouped setting cards, lazy page creation with error boundaries, Save/Apply footer.
/// </summary>
public sealed partial class SettingsForm : ModernForm
{
    private sealed class SettingsPageDef
    {
        public string Name { get; }
        public char Glyph { get; }
        public Func<StackPanel> Factory { get; }
        public Action<AppSettings> Loader { get; }
        public Action<AppSettings> Saver { get; }
        public StackPanel? Panel { get; set; }
        public bool Failed { get; set; }

        public SettingsPageDef(string name, char glyph, Func<StackPanel> factory, Action<AppSettings> loader, Action<AppSettings> saver)
        {
            Name = name;
            Glyph = glyph;
            Factory = factory;
            Loader = loader;
            Saver = saver;
        }
    }

    private readonly ISettingsService _settingsService;
    private readonly IAudioEngine? _audioEngine;
    private readonly ToolTip _toolTip = ModernToolTip.Create();
    private readonly System.Windows.Forms.Timer _meterTimer;
    private readonly NavigationList _nav;
    private readonly SurfacePanel _host;
    private readonly List<SettingsPageDef> _pages;
    private AppSettings _workingCopy;

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

        _host = new SurfacePanel { Size = new Size(688, 588), Dock = DockStyle.Fill };

        // ── Pages definition ──────────────────────────────────────────────────────────
        _pages =
        [
            new("General", Glyphs.Settings, BuildGeneralPage, LoadGeneralSettings, SaveGeneralSettings),
            new("Pause & resume", Glyphs.Pause, BuildPauseResumePage, LoadPauseResumeSettings, SavePauseResumeSettings),
            new("Meetings", Glyphs.Headphones, BuildMeetingsPage, LoadMeetingsSettings, SaveMeetingsSettings),
            new("Video", Glyphs.Video, BuildVideoPage, LoadVideoSettings, SaveVideoSettings),
            new("Audio", Glyphs.Volume, BuildAudioPage, LoadAudioSettings, SaveAudioSettings),
            new("Recordings", Glyphs.HardDrive, BuildRecordingsPage, LoadRecordingsSettings, SaveRecordingsSettings),
            new("Notifications", Glyphs.Flag, BuildNotificationsPage, LoadNotificationsSettings, SaveNotificationsSettings),
            new("Shortcuts", Glyphs.Keyboard, BuildShortcutsPage, LoadShortcutsSettings, SaveShortcutsSettings),
            new("Advanced", Glyphs.Diagnostic, BuildAdvancedPage, LoadAdvancedSettings, SaveAdvancedSettings),
            new("About", Glyphs.Info, BuildAboutPage, LoadAboutSettings, SaveAboutSettings)
        ];

        // ── Footer ───────────────────────────────────────────────────────────────────
        var footer = new SurfacePanel { Size = new Size(688, 64), Dock = DockStyle.Bottom, TopDivider = true };
        _lblValidation.Bounds = new Rectangle(24, 22, 300, 20);
        _lblValidation.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

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
        _nav = new NavigationList { Bounds = new Rectangle(10, 76, 212, 540), AccessibleName = "Settings sections" };
        foreach (var page in _pages)
        {
            _nav.AddItem(page.Name, page.Glyph);
        }
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        sidebar.Controls.AddRange([sidebarTitle, _nav]);

        Controls.Add(_host);
        Controls.Add(footer);
        Controls.Add(sidebar);

        _meterTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _meterTimer.Tick += (_, _) => UpdateMeters();

        FormClosing += (_, _) =>
        {
            _meterTimer.Stop();
            _audioEngine?.SetMonitoring(this, false);
        };

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

    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;

        var page = _pages[index];
        if (page.Panel == null && !page.Failed)
        {
            try
            {
                var panel = page.Factory();
                page.Panel = panel;
                page.Loader(_workingCopy);
                _host.Controls.Add(panel);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to build Settings page {Page}", page.Name);
                page.Failed = true;

                var errPanel = CreatePage(page.Name, "This settings page could not be displayed.");
                var btnCopy = new ModernButton("Copy details", ButtonKind.Secondary, Glyphs.Copy) { AutoSize = true };
                btnCopy.Click += (_, _) =>
                {
                    try { Clipboard.SetText($"{ex.GetType().FullName}: {ex.Message}\r\n\r\n{ex.StackTrace}"); }
                    catch { }
                };
                var card = Card(
                    Row("Page error", ex.Message, btnCopy, Glyphs.Diagnostic)
                );
                errPanel.Controls.Add(card);
                page.Panel = errPanel;
                _host.Controls.Add(errPanel);
            }
        }

        for (var i = 0; i < _pages.Count; i++)
        {
            if (_pages[i].Panel != null)
            {
                _pages[i].Panel!.Visible = (i == index);
            }
        }

        var isAudio = string.Equals(page.Name, "Audio", StringComparison.OrdinalIgnoreCase);
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

    public void SelectPage(string name)
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            if (string.Equals(_pages[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _nav.SelectedIndex = i;
                ShowPage(i);
                break;
            }
        }
    }

    private void LoadSettingsIntoUi()
    {
        foreach (var page in _pages)
        {
            if (page.Panel != null && !page.Failed)
            {
                page.Loader(_workingCopy);
            }
        }
        _lblValidation.Text = string.Empty;
    }

    private bool SaveWorkingCopy()
    {
        foreach (var page in _pages)
        {
            if (page.Panel != null && !page.Failed)
            {
                page.Saver(_workingCopy);
            }
        }

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

        // Keep editing a private copy
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

    private void PopulateAudioDeviceLists()
    {
        _cmbMicDevice.Items.Clear();
        _micDeviceIds.Clear();
        var captureDevices = AudioDeviceLister.ListCaptureDevices().ToList();
        if (captureDevices.Count == 0)
        {
            _cmbMicDevice.Items.Add("(no microphones found)");
            _micDeviceIds.Add(null);
        }
        else
        {
            foreach (var device in captureDevices)
            {
                _cmbMicDevice.Items.Add(device.Name);
                _micDeviceIds.Add(device.Id);
            }
        }

        _cmbOutputDevice.Items.Clear();
        _outputDeviceIds.Clear();
        var renderDevices = AudioDeviceLister.ListRenderDevices().ToList();
        if (renderDevices.Count == 0)
        {
            _cmbOutputDevice.Items.Add("(no output devices found)");
            _outputDeviceIds.Add(null);
        }
        else
        {
            foreach (var device in renderDevices)
            {
                _cmbOutputDevice.Items.Add(device.Name);
                _outputDeviceIds.Add(device.Id);
            }
        }
    }

    private void UpdateAudioDeviceRowVisibility()
    {
        if (_micDeviceRow != null)
            _micDeviceRow.Visible = ValueAt(_micModeValues, _cmbMicMode.SelectedIndex, MicMode.DefaultCommunications) == MicMode.Specific;
        if (_outputDeviceRow != null)
            _outputDeviceRow.Visible = ValueAt(_outputModeValues, _cmbOutputMode.SelectedIndex, OutputMode.DefaultPlusCommunications) == OutputMode.Specific;
    }

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

    private static string FormatGain(int value) => value > 0
        ? string.Create(CultureInfo.CurrentCulture, $"+{value} dB")
        : string.Create(CultureInfo.CurrentCulture, $"{value} dB");

    private static int SelectValue<T>(ModernComboBox combo, IList<T> values, T current, Func<T, string> missingLabel)
    {
        var index = -1;
        for (var i = 0; i < values.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(values[i], current))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            combo.Items.Add(missingLabel(current));
            values.Add(current);
            index = values.Count - 1;
        }

        return index;
    }

    private static T ValueAt<T>(IList<T> values, int index, T fallback)
    {
        return index >= 0 && index < values.Count ? values[index] : fallback;
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        return settings.Clone();
    }
}
