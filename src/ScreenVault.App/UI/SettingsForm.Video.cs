using System.Globalization;
using ScreenVault.App.Platform;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private readonly ModernComboBox _cmbMonitor = new();
    private readonly ModernComboBox _cmbFrameRate = new();
    private readonly ModernComboBox _cmbQuality = new();
    private readonly ModernComboBox _cmbEncoder = new();
    private readonly ModernButton _btnRedetectEncoder = new("Detect now", ButtonKind.Secondary, Glyphs.Refresh);
    private readonly ToggleSwitch _chkCaptureCursor = new();
    private readonly ToggleSwitch _chkDownscale = new();
    private readonly ToggleSwitch _chkHideAppFromCapture = new();
    private readonly ThemedListView _lstProbeResults = new();
    private SettingRow _encoderRow = null!;

    private readonly List<int> _frameRateValues = [5, 10, 15, 24, 30, 60];
    private readonly List<int> _monitorIndexValues = [];

    private StackPanel BuildVideoPage()
    {
        _cmbFrameRate.Items.AddRange(["5 fps", "10 fps", "15 fps (recommended)", "24 fps", "30 fps", "60 fps"]);
        _cmbQuality.Items.AddRange(["Small — lowest CPU and size", "Balanced (recommended)", "High — crisp small text"]);
        _cmbEncoder.Items.AddRange(["Auto", "nvenc-d3d11", "amf-d3d11", "qsv-hwmap", "nvenc-sysmem", "amf-sysmem", "qsv-sysmem", "x264"]);

        foreach (var combo in new[] { _cmbMonitor, _cmbFrameRate, _cmbQuality, _cmbEncoder })
        {
            combo.Width = 260;
        }

        PopulateMonitorList();

        var video = CreatePage("Video", "Frame rate, quality, display selection, and hardware encoders.");

        video.Controls.Add(Section("Capture"));
        video.Controls.Add(Card(
            Row("Monitor", "Which screen to record when you have more than one connected.", _cmbMonitor, Glyphs.Monitor),
            Row("Frame rate", "15 fps keeps files small. 60 fps offers maximum smoothness for motion.", _cmbFrameRate, Glyphs.Video),
            Row("Quality", "Higher quality keeps small text crisp but creates larger files.", _cmbQuality, Glyphs.Monitor),
            Row("Capture mouse cursor", "Show the pointer in recordings.", _chkCaptureCursor),
            Row("Downscale to 1080p", "Recommended for 4K screens to reduce CPU usage.", _chkDownscale),
            Row("Hide ScreenVault windows", "Hides ScreenVault settings and controls from recordings, screenshots, and screen sharing.", _chkHideAppFromCapture, Glyphs.Shield)));

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

        return video;
    }

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
            _cmbMonitor.Items.Add("Monitor 1 (Primary)");
            _monitorIndexValues.Add(0);
        }
    }

    private void LoadVideoSettings(AppSettings s)
    {
        _cmbMonitor.SelectedIndex = SelectValue(_cmbMonitor, _monitorIndexValues, s.Video.MonitorIndex,
            idx => $"Monitor {idx + 1} (not currently connected)");

        _cmbFrameRate.SelectedIndex = SelectValue(_cmbFrameRate, _frameRateValues, s.Video.FrameRate,
            fps => string.Create(CultureInfo.CurrentCulture, $"{fps} fps (current)"));

        _cmbQuality.SelectedIndex = s.Video.Quality switch
        {
            VideoQuality.Small => 0,
            VideoQuality.High => 2,
            _ => 1
        };

        _cmbEncoder.SelectedItem = s.Video.Encoder;
        if (_cmbEncoder.SelectedIndex < 0)
        {
            _cmbEncoder.SelectedIndex = 0;
        }

        _chkCaptureCursor.Checked = s.Video.CaptureCursor;
        _chkDownscale.Checked = s.Video.DownscaleTo1080p;
        _chkHideAppFromCapture.Checked = s.Video.HideAppFromCapture;
        UpdateEncoderDescription(s.Video.DetectedEncoderProfile);
        RefreshProbeDetails(EncoderProbe.LastResult?.Details);
    }

    private void SaveVideoSettings(AppSettings s)
    {
        s.Video.MonitorIndex = ValueAt(_monitorIndexValues, _cmbMonitor.SelectedIndex, 0);
        s.Video.FrameRate = ValueAt(_frameRateValues, _cmbFrameRate.SelectedIndex, 15);
        s.Video.Quality = _cmbQuality.SelectedIndex switch
        {
            0 => VideoQuality.Small,
            2 => VideoQuality.High,
            _ => VideoQuality.Balanced
        };
        s.Video.Encoder = _cmbEncoder.SelectedItem?.ToString() ?? "Auto";
        s.Video.CaptureCursor = _chkCaptureCursor.Checked;
        s.Video.DownscaleTo1080p = _chkDownscale.Checked;
        s.Video.HideAppFromCapture = _chkHideAppFromCapture.Checked;
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
        if (e.ColumnIndex != 1 || e.Item?.Tag is not bool success) return false;

        var p = Theme.Current;
        var color = success ? p.Success : p.Danger;
        var text = success ? "Works" : "Not available";

        using var brush = new SolidBrush(color);
        e.Graphics.DrawString(text, Typography.BodyStrong, brush, e.Bounds.Location);
        return true;
    }

    private void UpdateEncoderDescription(string? detectedProfile)
    {
        if (_encoderRow == null) return;
        _encoderRow.Description = string.IsNullOrWhiteSpace(detectedProfile)
            ? "Auto picks the fastest encoder that works on this PC."
            : $"Auto picks the fastest encoder. Currently using: {detectedProfile}.";
    }

    private async Task RedetectEncoderAsync()
    {
        _btnRedetectEncoder.Enabled = false;
        _btnRedetectEncoder.Text = "Testing…";
        try
        {
            var customPath = string.IsNullOrWhiteSpace(_workingCopy.Advanced.FfmpegPath) ? null : _workingCopy.Advanced.FfmpegPath.Trim();
            var paths = await Task.Run(() => new FfmpegLocator().Locate(customPath)).ConfigureAwait(true);
            var result = await EncoderProbe.ProbeAsync(paths.FfmpegPath).ConfigureAwait(true);
            if (IsDisposed) return;

            _workingCopy.Video.DetectedEncoderProfile = result.ProfileName;
            _workingCopy.Video.EncoderFingerprint = result.Fingerprint;
            UpdateEncoderDescription(result.ProfileName);
            RefreshProbeDetails(result.Details);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hardware encoder test failed.");
        }
        finally
        {
            _btnRedetectEncoder.Enabled = true;
            _btnRedetectEncoder.Text = "Detect now";
        }
    }
}
