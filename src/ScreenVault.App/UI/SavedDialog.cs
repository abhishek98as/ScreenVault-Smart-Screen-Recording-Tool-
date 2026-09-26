using System.Diagnostics;
using System.Globalization;
using System.IO;
using ScreenVault.App.Platform;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

public sealed class SavedDialog : Form
{
    private SessionManifest _manifest;
    private readonly ISettingsService _settingsService;
    private readonly ISessionStore? _sessionStore;
    private readonly PlayerLauncher _playerLauncher;

    private readonly TextBox _txtTitle;
    private readonly Label _lblFileName;
    private readonly PathEllipsisLabel _lblLocation;
    private readonly Label _lblStats;
    private readonly Label _lblStatus;
    private readonly Button _btnPlay;
    private readonly Button _btnShowInFolder;
    private readonly Button _btnSaveCopy;
    private readonly Button _btnMoveTo;
    private readonly Button _btnRename;
    private readonly Button _btnClose;
    private readonly ProgressBar _progressBar;
    private readonly ToolTip _toolTip;

    private string _targetFilePath;
    private bool _isFinalizing;

    public SavedDialog(
        SessionManifest manifest,
        ISettingsService settingsService,
        ISessionStore? sessionStore = null,
        PlayerLauncher? playerLauncher = null,
        Task<MergeResult?>? finalizeTask = null)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _sessionStore = sessionStore;
        _playerLauncher = playerLauncher ?? new PlayerLauncher(settingsService);
        _toolTip = new ToolTip();

        Text = "ScreenVault – Recording Saved";
        var appIcon = AppIcon.Get();
        if (appIcon != null) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(580, 380);
        TopMost = true;
        KeyPreview = true;

        // Resolve saved file path
        _targetFilePath = ResolveSavedFilePath(_manifest);

        // Header
        var lblHeader = new Label
        {
            Location = new Point(20, 16),
            Size = new Size(540, 26),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 142, 62),
            Text = "✔ Recording Saved",
            UseMnemonic = false
        };

        // Title row
        var lblTitlePrompt = new Label
        {
            Location = new Point(20, 52),
            Size = new Size(95, 22),
            Font = new Font("Segoe UI", 9f),
            Text = "Session Title:",
            UseMnemonic = false
        };

        _txtTitle = new TextBox
        {
            Location = new Point(120, 50),
            Size = new Size(340, 25),
            Font = new Font("Segoe UI", 9f),
            Text = _manifest.Title ?? string.Empty
        };

        _btnRename = new Button
        {
            Location = new Point(470, 49),
            Size = new Size(90, 27),
            Font = new Font("Segoe UI", 9f),
            Text = "Rename",
            UseMnemonic = false
        };
        _btnRename.Click += OnRenameClicked;
        _toolTip.SetToolTip(_btnRename, "Rename recording with new title");

        // Dedicated Info Labels: File, Location (with PathEllipsis), Stats, Status
        var lblFilePrompt = new Label
        {
            Location = new Point(20, 88),
            Size = new Size(75, 20),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Text = "File:",
            UseMnemonic = false
        };

        _lblFileName = new Label
        {
            Location = new Point(95, 88),
            Size = new Size(465, 20),
            Font = new Font("Segoe UI", 9f),
            AutoEllipsis = true,
            UseMnemonic = false
        };

        var lblLocationPrompt = new Label
        {
            Location = new Point(20, 114),
            Size = new Size(75, 20),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Text = "Location:",
            UseMnemonic = false
        };

        _lblLocation = new PathEllipsisLabel
        {
            Location = new Point(95, 114),
            Size = new Size(465, 20),
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand,
            UseMnemonic = false
        };

        var infoMenu = new ContextMenuStrip();
        infoMenu.Items.Add("Copy path", null, (_, _) =>
        {
            var dir = GetNormalizedLocation();
            if (!string.IsNullOrEmpty(dir))
            {
                Clipboard.SetText(dir);
            }
        });
        infoMenu.Items.Add("Copy Full File Path", null, (_, _) =>
        {
            if (!string.IsNullOrEmpty(_targetFilePath))
            {
                Clipboard.SetText(_targetFilePath);
            }
        });
        _lblLocation.ContextMenuStrip = infoMenu;

        _lblStats = new Label
        {
            Location = new Point(20, 142),
            Size = new Size(540, 42),
            Font = new Font("Segoe UI", 9f),
            UseMnemonic = false
        };

        _lblStatus = new Label
        {
            Location = new Point(20, 190),
            Size = new Size(540, 22),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = Color.DimGray,
            UseMnemonic = false
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(20, 218),
            Size = new Size(540, 14),
            Visible = false
        };

        // Action Buttons Row inside FlowLayoutPanel to avoid clipping
        var pnlButtons = new FlowLayoutPanel
        {
            Location = new Point(16, 246),
            Size = new Size(548, 48),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true
        };

        _btnPlay = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(88, 34),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Text = "▶ Play",
            Margin = new Padding(3, 3, 5, 3),
            UseMnemonic = false
        };
        _btnPlay.Click += (_, _) => _playerLauncher.Launch(_targetFilePath);
        _toolTip.SetToolTip(_btnPlay, "Play the final recording");

        _btnShowInFolder = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(116, 34),
            Font = new Font("Segoe UI", 9f),
            Text = "Show in Folder",
            Margin = new Padding(3, 3, 5, 3),
            UseMnemonic = false
        };
        _btnShowInFolder.Click += (_, _) => RevealFolder();
        _toolTip.SetToolTip(_btnShowInFolder, "Show in Folder (Ctrl+O)");

        _btnSaveCopy = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(116, 34),
            Font = new Font("Segoe UI", 9f),
            Text = "Save Copy As…",
            Margin = new Padding(3, 3, 5, 3),
            UseMnemonic = false
        };
        _btnSaveCopy.Click += async (_, _) => await OnSaveCopyClickedAsync(isMove: false);
        _toolTip.SetToolTip(_btnSaveCopy, "Save a copy of the video to another location");

        _btnMoveTo = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(95, 34),
            Font = new Font("Segoe UI", 9f),
            Text = "Move To…",
            Margin = new Padding(3, 3, 5, 3),
            UseMnemonic = false
        };
        _btnMoveTo.Click += async (_, _) => await OnSaveCopyClickedAsync(isMove: true);
        _toolTip.SetToolTip(_btnMoveTo, "Move the video file to another location");

        _btnClose = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(82, 34),
            Font = new Font("Segoe UI", 9f),
            Text = "Close",
            Margin = new Padding(3, 3, 3, 3),
            UseMnemonic = false
        };
        _btnClose.Click += (_, _) => Close();
        _toolTip.SetToolTip(_btnClose, "Close (Esc)");

        pnlButtons.Controls.Add(_btnPlay);
        pnlButtons.Controls.Add(_btnShowInFolder);
        pnlButtons.Controls.Add(_btnSaveCopy);
        pnlButtons.Controls.Add(_btnMoveTo);
        pnlButtons.Controls.Add(_btnClose);

        Controls.Add(lblHeader);
        Controls.Add(lblTitlePrompt);
        Controls.Add(_txtTitle);
        Controls.Add(_btnRename);
        Controls.Add(lblFilePrompt);
        Controls.Add(_lblFileName);
        Controls.Add(lblLocationPrompt);
        Controls.Add(_lblLocation);
        Controls.Add(_lblStats);
        Controls.Add(_lblStatus);
        Controls.Add(_progressBar);
        Controls.Add(pnlButtons);

        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
            else if (e.Control && e.KeyCode == Keys.O)
            {
                RevealFolder();
                e.Handled = true;
            }
        };

        // Check if finalizing in background
        if (finalizeTask != null && !finalizeTask.IsCompleted)
        {
            _isFinalizing = true;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.Visible = true;
            _btnPlay.Enabled = false;
            _btnShowInFolder.Enabled = false;
            _btnSaveCopy.Enabled = false;
            _btnMoveTo.Enabled = false;
            UpdateInfoDisplay();

            _ = finalizeTask.ContinueWith(t =>
            {
                if (!IsDisposed)
                {
                    try
                    {
                        BeginInvoke(OnFinalizationComplete);
                    }
                    catch
                    {
                        // Handle disposed form
                    }
                }
            }, TaskScheduler.Default);
        }
        else
        {
            _isFinalizing = false;
            UpdateInfoDisplay();
            var hasFile = File.Exists(_targetFilePath);
            _btnPlay.Enabled = hasFile;
            _btnShowInFolder.Enabled = hasFile || Directory.Exists(GetNormalizedLocation());
            _btnSaveCopy.Enabled = hasFile;
            _btnMoveTo.Enabled = hasFile;
        }
    }

    private void RevealFolder()
    {
        if (File.Exists(_targetFilePath))
        {
            Process.Start("explorer.exe", $"/select,\"{_targetFilePath}\"");
        }
        else
        {
            var dir = GetNormalizedLocation();
            if (Directory.Exists(dir))
            {
                Process.Start("explorer.exe", $"\"{dir}\"");
            }
        }
    }

    private void OnFinalizationComplete()
    {
        if (IsDisposed) return;

        _isFinalizing = false;
        _progressBar.Visible = false;

        // Reload manifest from store to pick up merged file and remux statuses
        if (_sessionStore != null)
        {
            var updated = _sessionStore.Load(_manifest.SessionId);
            if (updated != null)
            {
                _manifest = updated;
            }
        }

        _targetFilePath = ResolveSavedFilePath(_manifest);
        UpdateInfoDisplay();

        var hasFile = File.Exists(_targetFilePath);
        _btnPlay.Enabled = hasFile;
        _btnShowInFolder.Enabled = hasFile || Directory.Exists(GetNormalizedLocation());
        _btnSaveCopy.Enabled = hasFile;
        _btnMoveTo.Enabled = hasFile;
    }

    private string GetNormalizedLocation()
    {
        var rawLoc = !string.IsNullOrEmpty(_targetFilePath)
            ? Path.GetDirectoryName(_targetFilePath)
            : _manifest.Segments.FirstOrDefault()?.Location;

        if (string.IsNullOrEmpty(rawLoc)) return string.Empty;

        var expanded = Environment.ExpandEnvironmentVariables(rawLoc);
        return Path.GetFullPath(expanded);
    }

    private void UpdateInfoDisplay()
    {
        var totalBytes = _manifest.Segments.Sum(s => s.Bytes);
        var mb = totalBytes / (1024.0 * 1024.0);
        var durationSec = _manifest.Segments.Sum(s => s.DurationSec ?? 0.0);
        var duration = durationSec > 0
            ? TimeSpan.FromSeconds(durationSec)
            : (_manifest.EndedAtUtc ?? DateTime.UtcNow) - _manifest.StartedAtUtc;

        var location = GetNormalizedLocation();
        var fileName = _isFinalizing
            ? "Finalizing… (remuxing && merging)"
            : (!string.IsNullOrEmpty(_targetFilePath) ? Path.GetFileName(_targetFilePath) : "Finalizing…");

        _lblFileName.Text = fileName;
        _lblLocation.Text = location;
        _toolTip.SetToolTip(_lblLocation, !string.IsNullOrEmpty(_targetFilePath) ? _targetFilePath : location);

        _lblStats.Text = $"Duration: {duration:hh\\:mm\\:ss}   |   Size: {mb:F1} MB\n" +
                         $"Parts: {_manifest.Segments.Count}   |   Markers: {_manifest.Markers.Count}";

        if (_isFinalizing)
        {
            _lblStatus.Text = "Finalizing… (remuxing && merging)";
            _lblStatus.Visible = true;
        }
        else
        {
            _lblStatus.Text = string.Empty;
            _lblStatus.Visible = false;
        }
    }

    private static string ResolveSavedFilePath(SessionManifest manifest)
    {
        // 1. If merged path is specified and exists, that is primary
        if (!string.IsNullOrEmpty(manifest.MergedPath) && File.Exists(manifest.MergedPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(manifest.MergedPath));
        }

        // 2. If single or last part has a final path (.mkv / .mp4), use that
        var lastSeg = manifest.Segments.LastOrDefault();
        if (lastSeg != null)
        {
            var expLocation = Path.GetFullPath(Environment.ExpandEnvironmentVariables(lastSeg.Location));
            if (!string.IsNullOrEmpty(lastSeg.FinalPath))
            {
                var final = Path.IsPathRooted(lastSeg.FinalPath)
                    ? lastSeg.FinalPath
                    : Path.GetFullPath(Path.Combine(expLocation, lastSeg.FinalPath));
                return final;
            }

            if (!string.IsNullOrEmpty(lastSeg.TsPath))
            {
                var ts = Path.IsPathRooted(lastSeg.TsPath)
                    ? lastSeg.TsPath
                    : Path.GetFullPath(Path.Combine(expLocation, lastSeg.TsPath));
                return Path.ChangeExtension(ts, ".mkv");
            }
        }

        if (!string.IsNullOrEmpty(manifest.MergedPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(manifest.MergedPath));
        }

        return string.Empty;
    }

    private void OnRenameClicked(object? sender, EventArgs e)
    {
        var newTitle = _txtTitle.Text.Trim();
        if (string.IsNullOrWhiteSpace(newTitle)) return;

        // Sanitize title
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(newTitle.Select(c => invalid.Contains(c) ? '-' : c))
            .Replace(" ", "-");
        if (sanitized.Length > 60) sanitized = sanitized[..60];

        _manifest.Title = sanitized;
        _sessionStore?.Save(_manifest);

        UpdateInfoDisplay();
        MessageBox.Show(this, $"Session title updated to:\n{sanitized}", "Session Renamed", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task OnSaveCopyClickedAsync(bool isMove)
    {
        if (!File.Exists(_targetFilePath))
        {
            MessageBox.Show(this, "The target video file is not yet available.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            FileName = Path.GetFileName(_targetFilePath),
            Filter = "Video Files (*.mkv;*.mp4;*.ts)|*.mkv;*.mp4;*.ts|All Files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var destPath = sfd.FileName;
        _progressBar.Visible = true;
        _progressBar.Style = ProgressBarStyle.Blocks;
        _progressBar.Value = 0;

        try
        {
            var srcFi = new FileInfo(_targetFilePath);
            var totalBytes = srcFi.Length;
            long copiedBytes = 0;

            await Task.Run(async () =>
            {
                var buffer = new byte[4 * 1024 * 1024]; // 4 MB buffer
                await using var srcStream = new FileStream(_targetFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
                await using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);

                int read;
                while ((read = await srcStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await destStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    copiedBytes += read;
                    var percent = totalBytes > 0 ? (int)(copiedBytes * 100 / totalBytes) : 0;
                    Invoke(() => _progressBar.Value = Math.Clamp(percent, 0, 100));
                }
            });

            if (isMove)
            {
                try
                {
                    File.Delete(_targetFilePath);
                    _targetFilePath = destPath;
                    UpdateInfoDisplay();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not delete original file after move.");
                }
            }

            MessageBox.Show(this, $"File successfully {(isMove ? "moved" : "copied")} to:\n{destPath}", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to {(isMove ? "move" : "copy")} file:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _progressBar.Visible = false;
        }
    }
}

internal sealed class PathEllipsisLabel : Label
{
    public PathEllipsisLabel()
    {
        UseMnemonic = false;
        AutoEllipsis = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var flags = TextFormatFlags.PathEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
    }
}
