using System.Diagnostics;
using System.Globalization;
using System.IO;
using ScreenVault.App.Platform;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

/// <summary>Exports part of a recording (full, last 5 minutes, around a marker, or a custom range).</summary>
public sealed class ClipExportForm : ModernForm
{
    private const string FullSession = "Full recording";
    private const string LastFiveMinutes = "Last 5 minutes";
    private const string CustomRange = "Custom range";
    private const string MarkerPrefix = "Around marker: ";

    private readonly SessionManifest _manifest;
    private readonly IClipExporter _clipExporter;
    private readonly PlayerLauncher _playerLauncher;

    private readonly ModernComboBox _cmbPreset;
    private readonly NumberField _numStartSec;
    private readonly NumberField _numEndSec;
    private readonly TextLabel _lblStartTime;
    private readonly TextLabel _lblEndTime;
    private readonly SegmentedControl _format;
    private readonly ToggleSwitch _chkPrecise;
    private readonly TextField _txtDest;
    private readonly ModernProgressBar _progressBar;
    private readonly TextLabel _lblStatus;
    private readonly ModernButton _btnExport;
    private readonly ModernButton _btnPlay;
    private readonly ModernButton _btnShowInFolder;

    private bool _updatingFromPreset;
    private string? _exportedFilePath;
    private CancellationTokenSource? _exportCts;

    public ClipExportForm(
        SessionManifest manifest,
        IClipExporter clipExporter,
        PlayerLauncher playerLauncher)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _clipExporter = clipExporter ?? throw new ArgumentNullException(nameof(clipExporter));
        _playerLauncher = playerLauncher ?? throw new ArgumentNullException(nameof(playerLauncher));

        Text = "ScreenVault – Export Clip";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 504);

        var totalDurationSec = _manifest.Segments.Sum(s => s.DurationSec ?? 0.0);
        if (totalDurationSec <= 0)
        {
            totalDurationSec = ((_manifest.EndedAtUtc ?? DateTime.UtcNow) - _manifest.StartedAtUtc).TotalSeconds;
        }

        if (totalDurationSec <= 0) totalDurationSec = 60.0;

        // ── Header ───────────────────────────────────────────────────────────────────
        var badge = new GlyphBadge { Glyph = Glyphs.Cut, Tone = Tone.Accent, Bounds = new Rectangle(28, 24, 44, 44) };
        var title = new TextLabel("Export a clip", Typography.Title) { Location = new Point(84, 22) };
        var sessionName = string.IsNullOrWhiteSpace(_manifest.Title)
            ? _manifest.StartedAtUtc.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture)
            : _manifest.Title;
        var subtitle = new TextLabel($"{sessionName} · {FormatTime(totalDurationSec)} long", Typography.Body, TextTone.Secondary)
        {
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(86, 50, 446, 20)
        };

        // ── Form fields ──────────────────────────────────────────────────────────────
        const int labelX = 28;
        const int fieldX = 176;

        _cmbPreset = new ModernComboBox { Bounds = new Rectangle(fieldX, 92, 356, 32) };
        _cmbPreset.Items.Add(FullSession);
        _cmbPreset.Items.Add(LastFiveMinutes);
        foreach (var m in _manifest.Markers)
        {
            var note = string.IsNullOrWhiteSpace(m.Note) ? "marker" : m.Note;
            _cmbPreset.Items.Add($"{MarkerPrefix}{note} ({FormatTime(m.OffsetSec)}) ± 2 min");
        }

        _cmbPreset.Items.Add(CustomRange);
        _cmbPreset.SelectedIndex = 0;
        _cmbPreset.SelectedIndexChanged += (_, _) => OnPresetChanged(totalDurationSec);

        _numStartSec = new NumberField { Bounds = new Rectangle(fieldX, 140, 150, 32), DecimalPlaces = 1, Maximum = (decimal)totalDurationSec, Suffix = "s" };
        _lblStartTime = new TextLabel(FormatTime(0), Typography.Body, TextTone.Secondary) { Location = new Point(fieldX + 162, 146) };
        _numEndSec = new NumberField { Bounds = new Rectangle(fieldX, 188, 150, 32), DecimalPlaces = 1, Maximum = (decimal)totalDurationSec, Value = (decimal)totalDurationSec, Suffix = "s" };
        _lblEndTime = new TextLabel(FormatTime(totalDurationSec), Typography.Body, TextTone.Secondary) { Location = new Point(fieldX + 162, 194) };
        _numStartSec.ValueChanged += (_, _) => OnRangeEdited();
        _numEndSec.ValueChanged += (_, _) => OnRangeEdited();

        _format = new SegmentedControl { Bounds = new Rectangle(fieldX, 236, 200, 34), AccessibleName = "Output format" };
        _format.AddItem("MP4");
        _format.AddItem("MKV");
        _format.SelectedIndex = 0;
        _format.SelectedIndexChanged += (_, _) => SyncDestinationExtension();

        _chkPrecise = new ToggleSwitch { ShowStateText = false, Location = new Point(fieldX - 2, 290) };

        var defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        _txtDest = new TextField { Bounds = new Rectangle(fieldX, 342, 262, 32), Text = Path.Combine(defaultFolder, $"Clip_{_manifest.SessionId}.mp4") };
        var btnBrowse = new ModernButton("Browse…", ButtonKind.Secondary) { Bounds = new Rectangle(446, 342, 86, 32) };
        btnBrowse.Click += (_, _) => BrowseDestination();

        _progressBar = new ModernProgressBar { Bounds = new Rectangle(28, 396, 504, 6), Visible = false };
        _lblStatus = new TextLabel("Choose a range and press Export.", Typography.Caption, TextTone.Secondary)
        {
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(28, 408, 504, 18)
        };

        Controls.AddRange(
        [
            badge, title, subtitle,
            FieldLabel("Range", labelX, 98), _cmbPreset,
            FieldLabel("Start", labelX, 146), _numStartSec, _lblStartTime,
            FieldLabel("End", labelX, 194), _numEndSec, _lblEndTime,
            FieldLabel("Format", labelX, 244), _format,
            FieldLabel("Precise cut", labelX, 286), new TextLabel("Exact start and end (re-encodes, slower)", Typography.Caption, TextTone.Secondary) { Location = new Point(labelX, 306) }, _chkPrecise,
            FieldLabel("Save to", labelX, 348), _txtDest, btnBrowse,
            _progressBar, _lblStatus
        ]);

        // ── Footer ───────────────────────────────────────────────────────────────────
        var footer = new SurfacePanel { Size = new Size(560, 68), Dock = DockStyle.Bottom, TopDivider = true };
        _btnPlay = new ModernButton("Play", ButtonKind.Subtle, Glyphs.Play) { Bounds = new Rectangle(16, 18, 84, 32), Enabled = false };
        _btnPlay.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_exportedFilePath)) _playerLauncher.Launch(_exportedFilePath);
        };
        _btnShowInFolder = new ModernButton("Show in folder", ButtonKind.Subtle, Glyphs.FolderOpen) { Bounds = new Rectangle(104, 18, 140, 32), Enabled = false };
        _btnShowInFolder.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_exportedFilePath) && File.Exists(_exportedFilePath))
            {
                Process.Start("explorer.exe", $"/select,\"{_exportedFilePath}\"");
            }
        };

        var btnClose = new ModernButton("Close", ButtonKind.Secondary) { Bounds = new Rectangle(328, 18, 88, 32), DialogResult = DialogResult.Cancel };
        _btnExport = new ModernButton("Export clip", ButtonKind.Primary, Glyphs.Export) { Bounds = new Rectangle(424, 18, 112, 32) };
        _btnExport.Click += async (_, _) => await RunExportAsync();
        footer.Controls.AddRange([_btnPlay, _btnShowInFolder, btnClose, _btnExport]);
        Controls.Add(footer);

        AcceptButton = _btnExport;
        CancelButton = btnClose;

        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing while exporting stops FFmpeg and removes the unfinished file.
        _exportCts?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exportCts?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static TextLabel FieldLabel(string text, int x, int y) => new(text, Typography.BodyStrong) { Location = new Point(x, y) };

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void OnRangeEdited()
    {
        _lblStartTime.Text = FormatTime((double)_numStartSec.Value);
        _lblEndTime.Text = FormatTime((double)_numEndSec.Value);
        if (!_updatingFromPreset && _cmbPreset.SelectedIndex != _cmbPreset.Items.Count - 1)
        {
            _cmbPreset.SelectedIndex = _cmbPreset.Items.Count - 1; // Custom range
        }
    }

    private void OnPresetChanged(double totalDurationSec)
    {
        var text = _cmbPreset.SelectedItem?.ToString() ?? string.Empty;
        _updatingFromPreset = true;
        try
        {
            if (text == FullSession)
            {
                _numStartSec.Value = 0;
                _numEndSec.Value = (decimal)totalDurationSec;
            }
            else if (text == LastFiveMinutes)
            {
                _numStartSec.Value = (decimal)Math.Max(0.0, totalDurationSec - 300.0);
                _numEndSec.Value = (decimal)totalDurationSec;
            }
            else if (text.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            {
                var markerIdx = _cmbPreset.SelectedIndex - 2;
                if (markerIdx >= 0 && markerIdx < _manifest.Markers.Count)
                {
                    var offset = _manifest.Markers[markerIdx].OffsetSec;
                    _numStartSec.Value = (decimal)Math.Max(0.0, offset - 120.0);
                    _numEndSec.Value = (decimal)Math.Min(totalDurationSec, offset + 120.0);
                }
            }
        }
        finally
        {
            _updatingFromPreset = false;
        }

        _lblStartTime.Text = FormatTime((double)_numStartSec.Value);
        _lblEndTime.Text = FormatTime((double)_numEndSec.Value);
    }

    private void SyncDestinationExtension()
    {
        var path = _txtDest.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var extension = _format.SelectedIndex == 1 ? ".mkv" : ".mp4";
        var current = Path.GetExtension(path);
        if (string.Equals(current, ".mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(current, ".mkv", StringComparison.OrdinalIgnoreCase))
        {
            _txtDest.Text = Path.ChangeExtension(path, extension);
        }
    }

    private void BrowseDestination()
    {
        var isMp4 = _format.SelectedIndex == 0;
        using var sfd = new SaveFileDialog
        {
            Filter = isMp4 ? "MP4 Video (*.mp4)|*.mp4" : "Matroska Video (*.mkv)|*.mkv",
            FileName = Path.GetFileName(_txtDest.Text),
            InitialDirectory = Path.GetDirectoryName(_txtDest.Text)
        };
        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            _txtDest.Text = sfd.FileName;
        }
    }

    private async Task RunExportAsync()
    {
        var start = (double)_numStartSec.Value;
        var end = (double)_numEndSec.Value;
        if (end <= start)
        {
            ModernDialog.Warning(this, "Check the range", "The end time must be later than the start time.");
            return;
        }

        var dest = _txtDest.Text.Trim();
        if (string.IsNullOrEmpty(dest))
        {
            ModernDialog.Warning(this, "Choose where to save", "Enter a file name or click Browse.");
            return;
        }

        _btnExport.Enabled = false;
        _btnPlay.Enabled = false;
        _btnShowInFolder.Enabled = false;
        _progressBar.Visible = true;
        _progressBar.Tone = Tone.Accent;
        _progressBar.Value = 10;
        _lblStatus.Text = "Exporting clip…";
        _lblStatus.Tone = TextTone.Secondary;

        var format = _format.SelectedIndex == 1 ? OutputContainerFormat.Mkv : OutputContainerFormat.Mp4;
        var options = new ClipExportOptions(
            _manifest.SessionId,
            start,
            end,
            dest,
            format,
            _chkPrecise.Checked);

        var progress = new Progress<double>(pct =>
        {
            if (!IsDisposed)
            {
                _progressBar.Value = (int)Math.Clamp(pct * 100, 0, 100);
            }
        });

        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();

        try
        {
            var result = await _clipExporter.ExportClipAsync(options, progress, _exportCts.Token).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (result.Success && !string.IsNullOrEmpty(result.FilePath))
            {
                _exportedFilePath = result.FilePath;
                _progressBar.Value = 100;
                _progressBar.Tone = Tone.Success;
                _lblStatus.Text = $"Clip saved as {Path.GetFileName(result.FilePath)}";
                _lblStatus.Tone = TextTone.Success;
                _btnPlay.Enabled = true;
                _btnShowInFolder.Enabled = true;
            }
            else
            {
                _lblStatus.Text = $"Export failed: {result.ErrorMessage}";
                _lblStatus.Tone = TextTone.Danger;
                ModernDialog.Error(this, "Export failed", result.ErrorMessage ?? "The clip could not be exported.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Clip export failed.");
            if (!IsDisposed)
            {
                _lblStatus.Text = $"Export error: {ex.Message}";
                _lblStatus.Tone = TextTone.Danger;
                ModernDialog.Error(this, "Export failed", ex.Message);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _btnExport.Enabled = true;
            }
        }
    }
}
