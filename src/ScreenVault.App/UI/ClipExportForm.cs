using System.Diagnostics;
using System.Globalization;
using System.IO;
using ScreenVault.App.Platform;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

public sealed class ClipExportForm : Form
{
    private readonly SessionManifest _manifest;
    private readonly IClipExporter _clipExporter;
    private readonly PlayerLauncher _playerLauncher;

    private readonly ComboBox _cmbPreset;
    private readonly NumericUpDown _numStartSec;
    private readonly NumericUpDown _numEndSec;
    private readonly ComboBox _cmbFormat;
    private readonly CheckBox _chkPrecise;
    private readonly TextBox _txtDest;
    private readonly Button _btnBrowse;
    private readonly ProgressBar _progressBar;
    private readonly Label _lblStatus;
    private readonly Button _btnExport;
    private readonly Button _btnPlay;
    private readonly Button _btnShowInFolder;
    private readonly Button _btnClose;

    private string? _exportedFilePath;

    public ClipExportForm(
        SessionManifest manifest,
        IClipExporter clipExporter,
        PlayerLauncher playerLauncher)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _clipExporter = clipExporter ?? throw new ArgumentNullException(nameof(clipExporter));
        _playerLauncher = playerLauncher ?? throw new ArgumentNullException(nameof(playerLauncher));

        Text = "ScreenVault – Export Clip";
        var appIcon = AppIcon.Get();
        if (appIcon != null) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 360);
        TopMost = true;

        var totalDurationSec = _manifest.Segments.Sum(s => s.DurationSec ?? 0.0);
        if (totalDurationSec <= 0)
        {
            totalDurationSec = ((_manifest.EndedAtUtc ?? DateTime.UtcNow) - _manifest.StartedAtUtc).TotalSeconds;
        }
        if (totalDurationSec <= 0) totalDurationSec = 60.0;

        var lblPreset = new Label { Text = "Range Preset:", Location = new Point(20, 20), AutoSize = true };
        _cmbPreset = new ComboBox { Location = new Point(140, 16), Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbPreset.Items.Add("Full Session");
        _cmbPreset.Items.Add("Last 5 Minutes");
        if (_manifest.Markers.Count > 0)
        {
            foreach (var m in _manifest.Markers)
            {
                var timeStr = TimeSpan.FromSeconds(m.OffsetSec).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                _cmbPreset.Items.Add($"Marker: {m.Note} ({timeStr}) ± 2 min");
            }
        }
        _cmbPreset.Items.Add("Custom Range");
        _cmbPreset.SelectedIndex = 0;
        _cmbPreset.SelectedIndexChanged += (_, _) => OnPresetChanged(totalDurationSec);

        var lblStart = new Label { Text = "Start time (seconds):", Location = new Point(20, 58), AutoSize = true };
        _numStartSec = new NumericUpDown { Location = new Point(140, 56), Width = 100, Maximum = (decimal)totalDurationSec, DecimalPlaces = 1 };

        var lblEnd = new Label { Text = "End time (seconds):", Location = new Point(20, 95), AutoSize = true };
        _numEndSec = new NumericUpDown { Location = new Point(140, 93), Width = 100, Maximum = (decimal)totalDurationSec, Value = (decimal)totalDurationSec, DecimalPlaces = 1 };

        var lblFormat = new Label { Text = "Output format:", Location = new Point(20, 132), AutoSize = true };
        _cmbFormat = new ComboBox { Location = new Point(140, 129), Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbFormat.Items.AddRange(["MP4 (Recommended)", "MKV"]);
        _cmbFormat.SelectedIndex = 0;

        _chkPrecise = new CheckBox { Text = "Precise re-encode (exact cut points, slower)", Location = new Point(140, 165), AutoSize = true };

        var lblDest = new Label { Text = "Destination:", Location = new Point(20, 198), AutoSize = true };
        var defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var defaultName = $"Clip_{_manifest.SessionId}.mp4";
        _txtDest = new TextBox { Location = new Point(140, 195), Width = 220, Text = Path.Combine(defaultFolder, defaultName) };
        _btnBrowse = new Button { Text = "Browse…", Location = new Point(365, 193), Width = 75, Height = 26 };
        _btnBrowse.Click += (_, _) =>
        {
            var isMp4 = _cmbFormat.SelectedIndex == 0;
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
        };

        _progressBar = new ProgressBar { Location = new Point(20, 235), Size = new Size(420, 14), Minimum = 0, Maximum = 100 };
        _lblStatus = new Label { Location = new Point(20, 255), Size = new Size(420, 20), Text = "Ready to export." };

        _btnExport = new Button
        {
            Location = new Point(20, 285),
            Size = new Size(110, 32),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            BackColor = Color.FromArgb(30, 142, 62),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Text = "Export Clip"
        };
        _btnExport.FlatAppearance.BorderSize = 0;
        _btnExport.Click += async (_, _) => await RunExportAsync();

        _btnPlay = new Button { Location = new Point(135, 285), Size = new Size(80, 32), Text = "▶ Play", Enabled = false };
        _btnPlay.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_exportedFilePath)) _playerLauncher.Launch(_exportedFilePath);
        };

        _btnShowInFolder = new Button { Location = new Point(220, 285), Size = new Size(115, 32), Text = "Show in Folder", Enabled = false };
        _btnShowInFolder.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_exportedFilePath) && File.Exists(_exportedFilePath))
            {
                Process.Start("explorer.exe", $"/select,\"{_exportedFilePath}\"");
            }
        };

        _btnClose = new Button { Location = new Point(340, 285), Size = new Size(80, 32), Text = "Close" };
        _btnClose.Click += (_, _) => Close();

        Controls.Add(lblPreset);
        Controls.Add(_cmbPreset);
        Controls.Add(lblStart);
        Controls.Add(_numStartSec);
        Controls.Add(lblEnd);
        Controls.Add(_numEndSec);
        Controls.Add(lblFormat);
        Controls.Add(_cmbFormat);
        Controls.Add(_chkPrecise);
        Controls.Add(lblDest);
        Controls.Add(_txtDest);
        Controls.Add(_btnBrowse);
        Controls.Add(_progressBar);
        Controls.Add(_lblStatus);
        Controls.Add(_btnExport);
        Controls.Add(_btnPlay);
        Controls.Add(_btnShowInFolder);
        Controls.Add(_btnClose);
    }

    private void OnPresetChanged(double totalDurationSec)
    {
        var text = _cmbPreset.SelectedItem?.ToString() ?? string.Empty;
        if (text == "Full Session")
        {
            _numStartSec.Value = 0;
            _numEndSec.Value = (decimal)totalDurationSec;
        }
        else if (text == "Last 5 Minutes")
        {
            _numStartSec.Value = (decimal)Math.Max(0.0, totalDurationSec - 300.0);
            _numEndSec.Value = (decimal)totalDurationSec;
        }
        else if (text.StartsWith("Marker:", StringComparison.OrdinalIgnoreCase))
        {
            // Find marker
            var markerIdx = _cmbPreset.SelectedIndex - 2;
            if (markerIdx >= 0 && markerIdx < _manifest.Markers.Count)
            {
                var offset = _manifest.Markers[markerIdx].OffsetSec;
                _numStartSec.Value = (decimal)Math.Max(0.0, offset - 120.0);
                _numEndSec.Value = (decimal)Math.Min(totalDurationSec, offset + 120.0);
            }
        }
    }

    private async Task RunExportAsync()
    {
        var start = (double)_numStartSec.Value;
        var end = (double)_numEndSec.Value;
        if (end <= start)
        {
            MessageBox.Show(this, "End time must be greater than start time.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var dest = _txtDest.Text.Trim();
        if (string.IsNullOrEmpty(dest))
        {
            MessageBox.Show(this, "Please choose a destination file path.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnExport.Enabled = false;
        _btnPlay.Enabled = false;
        _btnShowInFolder.Enabled = false;
        _progressBar.Value = 10;
        _lblStatus.Text = "Exporting clip…";

        var format = _cmbFormat.SelectedIndex == 1 ? OutputContainerFormat.Mkv : OutputContainerFormat.Mp4;
        var options = new ClipExportOptions(
            _manifest.SessionId,
            start,
            end,
            dest,
            format,
            _chkPrecise.Checked);

        var progress = new Progress<double>(pct =>
        {
            _progressBar.Value = (int)Math.Clamp(pct * 100, 0, 100);
        });

        try
        {
            var result = await _clipExporter.ExportClipAsync(options, progress).ConfigureAwait(true);
            if (result.Success && !string.IsNullOrEmpty(result.FilePath))
            {
                _exportedFilePath = result.FilePath;
                _progressBar.Value = 100;
                _lblStatus.Text = "✔ Clip exported successfully!";
                _btnPlay.Enabled = true;
                _btnShowInFolder.Enabled = true;
            }
            else
            {
                _lblStatus.Text = $"Export failed: {result.ErrorMessage}";
                MessageBox.Show(this, $"Export failed: {result.ErrorMessage}", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"Export error: {ex.Message}";
            MessageBox.Show(this, $"Export error: {ex.Message}", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnExport.Enabled = true;
        }
    }
}
