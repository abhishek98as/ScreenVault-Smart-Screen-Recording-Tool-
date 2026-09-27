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

/// <summary>Shown after a recording stops: name it, play it, or copy/move it somewhere else.</summary>
public sealed class SavedDialog : ModernForm
{
    private SessionManifest _manifest;
    private readonly ISettingsService _settingsService;
    private readonly ISessionStore? _sessionStore;
    private readonly PlayerLauncher _playerLauncher;

    private readonly GlyphBadge _badge;
    private readonly TextLabel _lblHeadline;
    private readonly TextLabel _lblSubtitle;
    private readonly TextField _txtTitle;
    private readonly TextLabel _lblFileName;
    private readonly PathEllipsisLabel _lblLocation;
    private readonly TextLabel _lblStats;
    private readonly TextLabel _lblStatus;
    private readonly ModernButton _btnPlay;
    private readonly ModernButton _btnShowInFolder;
    private readonly ModernButton _btnSaveCopy;
    private readonly ModernButton _btnMoveTo;
    private readonly ModernButton _btnRename;
    private readonly ModernButton _btnClose;
    private readonly ModernProgressBar _progressBar;
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
        _toolTip = ModernToolTip.Create();

        Text = "ScreenVault – Recording Saved";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 452);
        TopMost = true;
        KeyPreview = true;

        // Resolve saved file path
        _targetFilePath = ResolveSavedFilePath(_manifest);

        // ── Header ───────────────────────────────────────────────────────────────────
        _badge = new GlyphBadge { Glyph = Glyphs.CheckMark, Tone = Tone.Success, Filled = true, Bounds = new Rectangle(28, 26, 44, 44) };
        _lblHeadline = new TextLabel("Recording saved", Typography.Title) { Location = new Point(84, 24) };
        _lblSubtitle = new TextLabel(string.Empty, Typography.Body, TextTone.Secondary) { AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(86, 52, 486, 20) };

        // ── Title ────────────────────────────────────────────────────────────────────
        var lblTitlePrompt = new TextLabel("Title", Typography.BodyStrong) { Location = new Point(28, 96) };
        _txtTitle = new TextField
        {
            Bounds = new Rectangle(28, 120, 432, 34),
            Text = _manifest.Title ?? string.Empty,
            PlaceholderText = "Add a title so it's easy to find later",
            LeadingGlyph = Glyphs.Rename
        };
        _txtTitle.Inner.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                OnRenameClicked(this, EventArgs.Empty);
                e.Handled = e.SuppressKeyPress = true;
            }
        };
        _btnRename = new ModernButton("Rename", ButtonKind.Secondary) { Bounds = new Rectangle(468, 120, 104, 34) };
        _btnRename.Click += OnRenameClicked;
        _toolTip.SetToolTip(_btnRename, "Save the title (Enter)");

        // ── File details ─────────────────────────────────────────────────────────────
        var infoCard = new CardPanel { ManualLayout = true, Bounds = new Rectangle(28, 172, 544, 150) };
        infoCard.Controls.Add(new TextLabel("File", Typography.Caption, TextTone.Tertiary) { Location = new Point(16, 14) });
        _lblFileName = new TextLabel(string.Empty, Typography.BodyStrong) { AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(16, 32, 512, 20) };
        infoCard.Controls.Add(new TextLabel("Location", Typography.Caption, TextTone.Tertiary) { Location = new Point(16, 62) });
        _lblLocation = new PathEllipsisLabel { Bounds = new Rectangle(16, 80, 512, 20), Cursor = Cursors.Hand };
        _lblLocation.Click += (_, _) => RevealFolder();

        var infoMenu = new ContextMenuStrip();
        ModernMenu.Apply(infoMenu);
        infoMenu.Items.Add(ModernMenu.Item("Copy folder path", Glyphs.Copy, (_, _) =>
        {
            var dir = GetNormalizedLocation();
            if (!string.IsNullOrEmpty(dir))
            {
                Clipboard.SetText(dir);
            }
        }));
        infoMenu.Items.Add(ModernMenu.Item("Copy full file path", Glyphs.Copy, (_, _) =>
        {
            if (!string.IsNullOrEmpty(_targetFilePath))
            {
                Clipboard.SetText(_targetFilePath);
            }
        }));
        _lblLocation.ContextMenuStrip = infoMenu;
        Disposed += (_, _) => infoMenu.Dispose();

        _lblStats = new TextLabel(string.Empty, Typography.Body, TextTone.Secondary) { AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(16, 114, 512, 20) };
        infoCard.Controls.AddRange([_lblFileName, _lblLocation, _lblStats]);

        // ── Progress / status ────────────────────────────────────────────────────────
        _lblStatus = new TextLabel(string.Empty, Typography.Caption, TextTone.Secondary) { AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(28, 334, 544, 18) };
        _progressBar = new ModernProgressBar { Bounds = new Rectangle(28, 356, 544, 6), Visible = false };

        // ── Footer ───────────────────────────────────────────────────────────────────
        var footer = new SurfacePanel { Size = new Size(600, 68), Dock = DockStyle.Bottom, TopDivider = true };
        var secondaryActions = new FlowLayoutPanel { Bounds = new Rectangle(16, 16, 360, 40), WrapContents = false };
        _btnShowInFolder = FooterAction(secondaryActions, "Show in folder", Glyphs.FolderOpen, "Show in folder (Ctrl+O)");
        _btnShowInFolder.Click += (_, _) => RevealFolder();
        _btnSaveCopy = FooterAction(secondaryActions, "Save copy…", Glyphs.Copy, "Save a copy of the video somewhere else");
        _btnSaveCopy.Click += async (_, _) => await OnSaveCopyClickedAsync(isMove: false);
        _btnMoveTo = FooterAction(secondaryActions, "Move…", Glyphs.Move, "Move the video file to another folder");
        _btnMoveTo.Click += async (_, _) => await OnSaveCopyClickedAsync(isMove: true);

        _btnClose = new ModernButton("Close", ButtonKind.Secondary) { Bounds = new Rectangle(384, 18, 88, 32) };
        _btnClose.Click += (_, _) => Close();
        _toolTip.SetToolTip(_btnClose, "Close (Esc)");
        _btnPlay = new ModernButton("Play", ButtonKind.Primary, Glyphs.Play) { Bounds = new Rectangle(480, 18, 96, 32) };
        _btnPlay.Click += (_, _) => _playerLauncher.Launch(_targetFilePath);
        _toolTip.SetToolTip(_btnPlay, "Play the final recording");
        footer.Controls.AddRange([secondaryActions, _btnClose, _btnPlay]);

        Controls.AddRange([_badge, _lblHeadline, _lblSubtitle, lblTitlePrompt, _txtTitle, _btnRename, infoCard, _lblStatus, _progressBar, footer]);

        KeyDown += (_, e) =>
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

        ResumeLayout(false);
        PerformLayout();

        // Check if finalizing in background
        if (finalizeTask != null && !finalizeTask.IsCompleted)
        {
            _isFinalizing = true;
            _progressBar.Marquee = true;
            _progressBar.Visible = true;
            SetFileActionsEnabled(false);
            UpdateInfoDisplay();

            _ = finalizeTask.ContinueWith(_ =>
            {
                if (!IsDisposed)
                {
                    try
                    {
                        BeginInvoke(OnFinalizationComplete);
                    }
                    catch (InvalidOperationException)
                    {
                        // Form was closed before finalization finished.
                    }
                }
            }, TaskScheduler.Default);
        }
        else
        {
            _isFinalizing = false;
            UpdateInfoDisplay();
            UpdateFileActions();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private ModernButton FooterAction(FlowLayoutPanel host, string text, char glyph, string tooltip)
    {
        var button = new ModernButton(text, ButtonKind.Subtle, glyph) { AutoSize = true, Margin = new Padding(0, 2, 4, 0) };
        _toolTip.SetToolTip(button, tooltip);
        host.Controls.Add(button);
        return button;
    }

    private void SetFileActionsEnabled(bool enabled)
    {
        _btnPlay.Enabled = enabled;
        _btnShowInFolder.Enabled = enabled;
        _btnSaveCopy.Enabled = enabled;
        _btnMoveTo.Enabled = enabled;
    }

    private void UpdateFileActions()
    {
        var hasFile = File.Exists(_targetFilePath);
        _btnPlay.Enabled = hasFile;
        _btnShowInFolder.Enabled = hasFile || Directory.Exists(GetNormalizedLocation());
        _btnSaveCopy.Enabled = hasFile;
        _btnMoveTo.Enabled = hasFile;
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
        _progressBar.Marquee = false;
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
        UpdateFileActions();
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
            ? "Finalizing… (converting and merging parts)"
            : (!string.IsNullOrEmpty(_targetFilePath) ? Path.GetFileName(_targetFilePath) : "Finalizing…");

        _lblFileName.Text = fileName;
        _lblLocation.Text = location;
        _toolTip.SetToolTip(_lblLocation, (!string.IsNullOrEmpty(_targetFilePath) ? _targetFilePath : location) + Environment.NewLine + "Click to show in Explorer · right-click to copy the path");

        var size = mb >= 1024 ? string.Create(CultureInfo.CurrentCulture, $"{mb / 1024.0:F2} GB") : string.Create(CultureInfo.CurrentCulture, $"{mb:F1} MB");
        _lblStats.Text = $"{duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)} long  ·  {size}  ·  {_manifest.Segments.Count} part{(_manifest.Segments.Count == 1 ? string.Empty : "s")}  ·  {_manifest.Markers.Count} marker{(_manifest.Markers.Count == 1 ? string.Empty : "s")}";

        if (_isFinalizing)
        {
            _badge.Glyph = Glyphs.Save;
            _badge.Tone = Tone.Accent;
            _lblHeadline.Text = "Saving your recording…";
            _lblSubtitle.Text = "Converting and merging parts. You can close this window — it continues in the background.";
            _lblStatus.Text = "Finalizing… (converting and merging parts)";
            _lblStatus.Tone = TextTone.Secondary;
        }
        else
        {
            _badge.Glyph = Glyphs.CheckMark;
            _badge.Tone = Tone.Success;
            _lblHeadline.Text = "Recording saved";
            _lblSubtitle.Text = File.Exists(_targetFilePath) ? "Everything is safely on disk." : "The file will appear once post-processing finishes.";
            _lblStatus.Text = string.Empty;
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
                return Path.IsPathRooted(lastSeg.FinalPath)
                    ? lastSeg.FinalPath
                    : Path.GetFullPath(Path.Combine(expLocation, lastSeg.FinalPath));
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
            .Replace(" ", "-", StringComparison.Ordinal);
        if (sanitized.Length > 60) sanitized = sanitized[..60];

        _manifest.Title = sanitized;
        _sessionStore?.Save(_manifest);

        UpdateInfoDisplay();
        _lblStatus.Text = $"Title saved as \"{sanitized}\".";
        _lblStatus.Tone = TextTone.Success;
    }

    private async Task OnSaveCopyClickedAsync(bool isMove)
    {
        if (!File.Exists(_targetFilePath))
        {
            ModernDialog.Warning(this, "The video isn't ready yet", "Wait until finalizing has finished, then try again.");
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
        _progressBar.Marquee = false;
        _progressBar.Value = 0;
        _progressBar.Visible = true;
        _lblStatus.Text = isMove ? "Moving…" : "Copying…";
        _lblStatus.Tone = TextTone.Secondary;
        SetFileActionsEnabled(false);

        try
        {
            var totalBytes = new FileInfo(_targetFilePath).Length;
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
                    BeginInvoke(() => _progressBar.Value = Math.Clamp(percent, 0, 100));
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

            _lblStatus.Text = $"{(isMove ? "Moved" : "Copied")} to {destPath}";
            _lblStatus.Tone = TextTone.Success;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Save copy / move failed.");
            ModernDialog.Error(this, $"Could not {(isMove ? "move" : "copy")} the file", ex.Message);
            _lblStatus.Text = string.Empty;
        }
        finally
        {
            _progressBar.Visible = false;
            UpdateFileActions();
        }
    }
}

/// <summary>Label that shortens long paths in the middle ("C:\Users\…\Screen Recordings").</summary>
internal sealed class PathEllipsisLabel : Label, IThemeAware
{
    public PathEllipsisLabel()
    {
        UseMnemonic = false;
        AutoEllipsis = true;
        Font = Typography.Body;
        ApplyTheme();
    }

    public void ApplyTheme() => ForeColor = Theme.Current.Text;

    protected override void OnPaint(PaintEventArgs e)
    {
        var flags = TextFormatFlags.PathEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
    }
}
