using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using ScreenVault.App.Platform;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Serilog;

namespace ScreenVault.App.UI;

public sealed class LibraryForm : Form
{
    private readonly ISessionStore _sessionStore;
    private readonly IRecordingController _controller;
    private readonly ISettingsService _settingsService;
    private readonly PlayerLauncher _playerLauncher;
    private readonly IClipExporter _clipExporter;

    private readonly TextBox _txtSearch;
    private readonly Button _btnRefresh;
    private readonly Label _lblCount;
    private readonly ListView _lstSessions;

    // Detail pane controls
    private readonly Label _lblDetailTitle;
    private readonly Label _lblDetailMeta;
    private readonly TabControl _tabDetails;
    private readonly ListView _lstMarkers;
    private readonly ListView _lstParts;
    private readonly ListView _lstEvents;

    private readonly Button _btnPlay;
    private readonly Button _btnShowInFolder;
    private readonly Button _btnExportClip;
    private readonly Button _btnMerge;
    private readonly Button _btnRename;
    private readonly Button _btnProtect;
    private readonly Button _btnDelete;

    private List<SessionManifest> _allSessions = [];
    private SessionManifest? _selectedSession;

    public LibraryForm(
        ISessionStore sessionStore,
        IRecordingController controller,
        ISettingsService settingsService,
        PlayerLauncher? playerLauncher = null,
        IClipExporter? clipExporter = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _playerLauncher = playerLauncher ?? new PlayerLauncher(settingsService);
        _clipExporter = clipExporter ?? new ClipExporter(sessionStore);

        Text = "ScreenVault – Recordings Library";
        var appIcon = AppIcon.Get();
        if (appIcon != null) Icon = appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 640);
        MinimumSize = new Size(800, 500);
        KeyPreview = true;

        var toolTip = new ToolTip();

        // Top bar
        var pnlTop = new Panel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(12, 8, 12, 8) };
        var lblSearch = new Label { Text = "Search:", Location = new Point(12, 12), AutoSize = true, Font = new Font("Segoe UI", 9f), UseMnemonic = false };
        _txtSearch = new TextBox { Location = new Point(65, 10), Width = 280, PlaceholderText = "Filter by title, marker, or session ID…" };
        _txtSearch.TextChanged += (_, _) => FilterSessions();

        _btnRefresh = new Button { Text = "Refresh", Location = new Point(355, 9), Width = 80, Height = 26, UseMnemonic = false };
        _btnRefresh.Click += async (_, _) => await LoadSessionsAsync();
        toolTip.SetToolTip(_btnRefresh, "Reload recording sessions from storage");

        _lblCount = new Label { Location = new Point(450, 13), AutoSize = true, ForeColor = Color.DimGray, Text = "Loading recordings…", UseMnemonic = false };

        pnlTop.Controls.AddRange([lblSearch, _txtSearch, _btnRefresh, _lblCount]);

        // Main split container (Left: Sessions list, Right: Details)
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 580,
            FixedPanel = FixedPanel.None
        };

        // Left: Sessions ListView
        _lstSessions = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false
        };
        _lstSessions.Columns.Add("Title", 180);
        _lstSessions.Columns.Add("Date && Time", 130);
        _lstSessions.Columns.Add("Duration", 75);
        _lstSessions.Columns.Add("Size", 75);
        _lstSessions.Columns.Add("Parts", 45);
        _lstSessions.Columns.Add("Markers", 60);
        _lstSessions.Columns.Add("Status", 80);
        _lstSessions.Columns.Add("🔒", 30);
        _lstSessions.SelectedIndexChanged += (_, _) => OnSessionSelectionChanged();

        split.Panel1.Controls.Add(_lstSessions);

        // Right: Detail Pane
        var pnlRight = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };

        _lblDetailTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Text = "Select a recording to view details",
            UseMnemonic = false
        };

        _lblDetailMeta = new Label
        {
            Dock = DockStyle.Top,
            Height = 45,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.DimGray,
            Text = string.Empty,
            UseMnemonic = false
        };

        // Bottom action buttons in detail pane
        var pnlActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 80,
            Padding = new Padding(4),
            AutoScroll = true
        };

        _btnPlay = new Button { Text = "▶ Play", Width = 80, Height = 32, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Enabled = false, UseMnemonic = false };
        _btnPlay.Click += (_, _) => PlaySelectedSession();
        toolTip.SetToolTip(_btnPlay, "Play selected recording");

        _btnShowInFolder = new Button { Text = "Show in Folder", Width = 105, Height = 32, Enabled = false, UseMnemonic = false };
        _btnShowInFolder.Click += (_, _) => ShowSelectedInFolder();
        toolTip.SetToolTip(_btnShowInFolder, "Show in Folder (Ctrl+O)");

        _btnExportClip = new Button { Text = "Export Clip…", Width = 95, Height = 32, Enabled = false, UseMnemonic = false };
        _btnExportClip.Click += (_, _) => ExportClipForSelected();
        toolTip.SetToolTip(_btnExportClip, "Export clip from selected recording…");

        _btnMerge = new Button { Text = "Merge Parts", Width = 95, Height = 32, Enabled = false, UseMnemonic = false };
        _btnMerge.Click += async (_, _) => await MergeSelectedSessionAsync();
        toolTip.SetToolTip(_btnMerge, "Merge all parts into a single video file");

        _btnRename = new Button { Text = "Rename Title…", Width = 100, Height = 32, Enabled = false, UseMnemonic = false };
        _btnRename.Click += (_, _) => RenameSelectedTitle();
        toolTip.SetToolTip(_btnRename, "Rename session title…");

        _btnProtect = new Button { Text = "🔒 Protect", Width = 90, Height = 32, Enabled = false, UseMnemonic = false };
        _btnProtect.Click += (_, _) => ToggleProtectSelected();
        toolTip.SetToolTip(_btnProtect, "Toggle protection from retention cleanup");

        _btnDelete = new Button { Text = "Delete", Width = 80, Height = 32, ForeColor = Color.DarkRed, Enabled = false, UseMnemonic = false };
        _btnDelete.Click += (_, _) => DeleteSelectedSession();
        toolTip.SetToolTip(_btnDelete, "Delete recording (send to Recycle Bin)");

        pnlActions.Controls.AddRange([_btnPlay, _btnShowInFolder, _btnExportClip, _btnMerge, _btnRename, _btnProtect, _btnDelete]);

        // Center tabs: Markers, Parts, Events
        _tabDetails = new TabControl { Dock = DockStyle.Fill };

        // Markers Tab
        var tabMarkers = new TabPage("Markers");
        _lstMarkers = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lstMarkers.Columns.Add("Time", 75);
        _lstMarkers.Columns.Add("Note", 190);
        _lstMarkers.Columns.Add("Kind", 60);
        _lstMarkers.DoubleClick += (_, _) => JumpToSelectedMarker();

        var pnlMarkerActions = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(4) };
        var btnExportMarkers = new Button { Text = "Export Markers…", Dock = DockStyle.Right, Width = 120, UseMnemonic = false };
        btnExportMarkers.Click += (_, _) => ExportMarkersForSelected();
        toolTip.SetToolTip(btnExportMarkers, "Export markers to text file");
        pnlMarkerActions.Controls.Add(btnExportMarkers);

        KeyDown += (s, e) =>
        {
            if (e.Control && e.KeyCode == Keys.O)
            {
                if (_btnShowInFolder.Enabled)
                {
                    ShowSelectedInFolder();
                    e.Handled = true;
                }
            }
        };

        tabMarkers.Controls.Add(_lstMarkers);
        tabMarkers.Controls.Add(pnlMarkerActions);

        // Parts Tab
        var tabParts = new TabPage("Parts");
        _lstParts = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lstParts.Columns.Add("Part", 45);
        _lstParts.Columns.Add("Size", 75);
        _lstParts.Columns.Add("Duration", 75);
        _lstParts.Columns.Add("Status", 75);
        _lstParts.Columns.Add("File", 150);
        tabParts.Controls.Add(_lstParts);

        // Events Tab
        var tabEvents = new TabPage("Timeline Events");
        _lstEvents = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lstEvents.Columns.Add("Time", 75);
        _lstEvents.Columns.Add("Type", 95);
        _lstEvents.Columns.Add("Detail", 200);
        tabEvents.Controls.Add(_lstEvents);

        _tabDetails.TabPages.AddRange([tabMarkers, tabParts, tabEvents]);

        pnlRight.Controls.Add(_tabDetails);
        pnlRight.Controls.Add(_lblDetailMeta);
        pnlRight.Controls.Add(_lblDetailTitle);
        pnlRight.Controls.Add(pnlActions);

        split.Panel2.Controls.Add(pnlRight);

        Controls.Add(split);
        Controls.Add(pnlTop);

        Shown += async (_, _) => await LoadSessionsAsync();
    }

    public async Task LoadSessionsAsync()
    {
        _btnRefresh.Enabled = false;
        _lblCount.Text = "Loading sessions…";

        try
        {
            _allSessions = await Task.Run(() => _sessionStore.LoadAllCanonical().ToList()).ConfigureAwait(true);
            FilterSessions();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load sessions in LibraryForm");
            _lblCount.Text = "Failed to load sessions.";
        }
        finally
        {
            _btnRefresh.Enabled = true;
        }
    }

    private void FilterSessions()
    {
        _lstSessions.BeginUpdate();
        _lstSessions.Items.Clear();

        var query = _txtSearch.Text.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _allSessions
            : _allSessions.Where(s =>
                (s.Title != null && s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                s.SessionId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Markers.Any(m => m.Note.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (var session in matches)
        {
            var title = string.IsNullOrWhiteSpace(session.Title) ? $"Session {session.SessionId}" : session.Title;
            var localStart = session.StartedAtUtc.ToLocalTime();
            var timeStr = localStart.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            var totalDurationSec = session.Segments.Sum(seg => seg.DurationSec ?? 0.0);
            if (totalDurationSec <= 0 && session.EndedAtUtc.HasValue)
            {
                totalDurationSec = (session.EndedAtUtc.Value - session.StartedAtUtc).TotalSeconds;
            }
            var durStr = totalDurationSec > 0
                ? TimeSpan.FromSeconds(totalDurationSec).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : "—";

            var totalBytes = session.Segments.Sum(seg => seg.Bytes);
            var mb = totalBytes / (1024.0 * 1024.0);
            var sizeStr = mb >= 1024 ? $"{mb / 1024.0:F2} GB" : $"{mb:F1} MB";

            var item = new ListViewItem(title) { Tag = session };
            item.SubItems.Add(timeStr);
            item.SubItems.Add(durStr);
            item.SubItems.Add(sizeStr);
            item.SubItems.Add(session.Segments.Count.ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(session.Markers.Count.ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(session.Status);
            item.SubItems.Add(session.Protected ? "🔒" : string.Empty);

            _lstSessions.Items.Add(item);
        }

        _lstSessions.EndUpdate();
        _lblCount.Text = $"{matches.Count} recording(s)";

        if (_lstSessions.Items.Count > 0 && _selectedSession == null)
        {
            _lstSessions.Items[0].Selected = true;
        }
        else
        {
            UpdateDetailPane();
        }
    }

    private void OnSessionSelectionChanged()
    {
        if (_lstSessions.SelectedItems.Count > 0 && _lstSessions.SelectedItems[0].Tag is SessionManifest session)
        {
            _selectedSession = session;
        }
        else
        {
            _selectedSession = null;
        }

        UpdateDetailPane();
    }

    private void UpdateDetailPane()
    {
        if (_selectedSession == null)
        {
            _lblDetailTitle.Text = "Select a recording";
            _lblDetailMeta.Text = string.Empty;
            _btnPlay.Enabled = false;
            _btnShowInFolder.Enabled = false;
            _btnExportClip.Enabled = false;
            _btnMerge.Enabled = false;
            _btnRename.Enabled = false;
            _btnProtect.Enabled = false;
            _btnDelete.Enabled = false;
            _lstMarkers.Items.Clear();
            _lstParts.Items.Clear();
            _lstEvents.Items.Clear();
            return;
        }

        var s = _selectedSession;
        _lblDetailTitle.Text = string.IsNullOrWhiteSpace(s.Title) ? $"Session {s.SessionId}" : s.Title;
        _lblDetailMeta.Text = $"ID: {s.SessionId}  ·  Machine: {s.Machine}  ·  Encoder: {s.Video.EncoderProfile} ({s.Video.Fps} fps)\n" +
                              $"Started: {s.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  Ended: {s.EndedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        _btnPlay.Enabled = true;
        _btnShowInFolder.Enabled = true;
        _btnExportClip.Enabled = true;
        _btnMerge.Enabled = s.Segments.Count > 1 && string.IsNullOrEmpty(s.MergedPath);
        _btnRename.Enabled = true;
        _btnProtect.Enabled = true;
        _btnProtect.Text = s.Protected ? "🔓 Unprotect" : "🔒 Protect";

        // Delete allowed only if not active recording session
        var isActive = _controller.State != RecorderState.Idle &&
                       (_controller.Health.CurrentFilePath?.Contains(s.SessionId, StringComparison.OrdinalIgnoreCase) ?? false);
        _btnDelete.Enabled = !isActive;

        // Populate Markers
        _lstMarkers.BeginUpdate();
        _lstMarkers.Items.Clear();
        foreach (var m in s.Markers)
        {
            var offset = TimeSpan.FromSeconds(m.OffsetSec).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            var item = new ListViewItem(offset) { Tag = m };
            item.SubItems.Add(m.Note);
            item.SubItems.Add(m.Kind);
            _lstMarkers.Items.Add(item);
        }
        _lstMarkers.EndUpdate();

        // Populate Parts
        _lstParts.BeginUpdate();
        _lstParts.Items.Clear();
        foreach (var seg in s.Segments)
        {
            var mb = seg.Bytes / (1024.0 * 1024.0);
            var dur = seg.DurationSec.HasValue ? $"{seg.DurationSec.Value:F0}s" : "—";
            var item = new ListViewItem($"Part {seg.Index}");
            item.SubItems.Add($"{mb:F1} MB");
            item.SubItems.Add(dur);
            item.SubItems.Add(seg.Remux);
            item.SubItems.Add(Path.GetFileName(!string.IsNullOrEmpty(seg.FinalPath) ? seg.FinalPath : seg.TsPath));
            _lstParts.Items.Add(item);
        }
        _lstParts.EndUpdate();

        // Populate Events
        _lstEvents.BeginUpdate();
        _lstEvents.Items.Clear();
        foreach (var evt in s.Events)
        {
            var timeStr = evt.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var item = new ListViewItem(timeStr);
            item.SubItems.Add(evt.Type);
            item.SubItems.Add(evt.Detail);
            _lstEvents.Items.Add(item);
        }
        _lstEvents.EndUpdate();
    }

    private static string ResolvePrimaryPlaybackFile(SessionManifest session)
    {
        if (!string.IsNullOrEmpty(session.MergedPath))
        {
            var expMerged = Path.GetFullPath(Environment.ExpandEnvironmentVariables(session.MergedPath));
            if (File.Exists(expMerged))
            {
                return expMerged;
            }
        }

        foreach (var seg in session.Segments.OrderBy(s => s.Index))
        {
            var expLocation = Path.GetFullPath(Environment.ExpandEnvironmentVariables(seg.Location));
            if (!string.IsNullOrEmpty(seg.FinalPath))
            {
                var fullFinal = Path.IsPathRooted(seg.FinalPath)
                    ? seg.FinalPath
                    : Path.GetFullPath(Path.Combine(expLocation, seg.FinalPath));
                if (File.Exists(fullFinal))
                {
                    return fullFinal;
                }
            }
            if (!string.IsNullOrEmpty(seg.TsPath))
            {
                var fullTs = Path.IsPathRooted(seg.TsPath)
                    ? seg.TsPath
                    : Path.GetFullPath(Path.Combine(expLocation, seg.TsPath));
                if (File.Exists(fullTs))
                {
                    return fullTs;
                }
            }
        }

        return string.Empty;
    }

    private void PlaySelectedSession()
    {
        if (_selectedSession == null) return;
        var file = ResolvePrimaryPlaybackFile(_selectedSession);
        if (string.IsNullOrEmpty(file))
        {
            MessageBox.Show(this, "No playable video file found for this session.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _playerLauncher.Launch(file);
    }

    private void ShowSelectedInFolder()
    {
        if (_selectedSession == null) return;
        var file = ResolvePrimaryPlaybackFile(_selectedSession);
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
        {
            Process.Start("explorer.exe", $"/select,\"{file}\"");
        }
        else if (_selectedSession.Segments.Count > 0)
        {
            var loc = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_selectedSession.Segments[0].Location));
            if (Directory.Exists(loc))
            {
                Process.Start("explorer.exe", $"\"{loc}\"");
            }
        }
    }

    private void JumpToSelectedMarker()
    {
        if (_selectedSession == null || _lstMarkers.SelectedItems.Count == 0) return;
        if (_lstMarkers.SelectedItems[0].Tag is not MarkerEntry marker) return;

        var preRoll = _settingsService.Current.Playback.MarkerPreRollSec;

        // If merged file exists, seek directly in merged file
        if (!string.IsNullOrEmpty(_selectedSession.MergedPath))
        {
            var expMerged = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_selectedSession.MergedPath));
            if (File.Exists(expMerged))
            {
                var targetSeek = Math.Max(0.0, marker.OffsetSec - preRoll);
                _playerLauncher.Launch(expMerged, targetSeek);
                return;
            }
        }

        // Otherwise find which part contains this marker
        double runningDuration = 0.0;
        foreach (var seg in _selectedSession.Segments.OrderBy(s => s.Index))
        {
            var dur = seg.DurationSec ?? (seg.EndedAtUtc.HasValue ? (seg.EndedAtUtc.Value - seg.StartedAtUtc).TotalSeconds : 600.0);
            if (marker.OffsetSec >= runningDuration && marker.OffsetSec <= (runningDuration + dur))
            {
                var localOffset = marker.OffsetSec - runningDuration;
                var targetSeek = Math.Max(0.0, localOffset - preRoll);
                var expLocation = Path.GetFullPath(Environment.ExpandEnvironmentVariables(seg.Location));
                var segFile = !string.IsNullOrEmpty(seg.FinalPath) ? seg.FinalPath : seg.TsPath;
                if (!string.IsNullOrEmpty(segFile))
                {
                    var full = Path.IsPathRooted(segFile) ? segFile : Path.GetFullPath(Path.Combine(expLocation, segFile));
                    if (File.Exists(full))
                    {
                        _playerLauncher.Launch(full, targetSeek);
                        return;
                    }
                }
            }
            runningDuration += dur;
        }

        // Fallback to primary
        var fallback = ResolvePrimaryPlaybackFile(_selectedSession);
        if (!string.IsNullOrEmpty(fallback))
        {
            _playerLauncher.Launch(fallback, Math.Max(0.0, marker.OffsetSec - preRoll));
        }
    }

    private void ExportClipForSelected()
    {
        if (_selectedSession == null) return;
        using var dlg = new ClipExportForm(_selectedSession, _clipExporter, _playerLauncher);
        dlg.ShowDialog(this);
    }

    private async Task MergeSelectedSessionAsync()
    {
        if (_selectedSession == null) return;

        _btnMerge.Enabled = false;
        _btnMerge.Text = "Merging…";

        try
        {
            var merger = new SessionMerger(_sessionStore, new FfprobeClient(), new ChapterWriter());
            var res = await merger.MergeSessionAsync(_selectedSession.SessionId).ConfigureAwait(true);
            if (res.Success && !string.IsNullOrEmpty(res.MergedFilePath))
            {
                _selectedSession.MergedPath = res.MergedFilePath;
                _selectedSession.MergeStatus = "Done";
                _sessionStore.Save(_selectedSession);
                MessageBox.Show(this, $"Session merged successfully into:\n{res.MergedFilePath}", "ScreenVault Merge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await LoadSessionsAsync();
            }
            else
            {
                MessageBox.Show(this, $"Merge failed: {res.ErrorMessage}", "ScreenVault Merge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error merging session: {ex.Message}", "ScreenVault Merge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnMerge.Text = "Merge Parts";
            _btnMerge.Enabled = true;
        }
    }

    private void RenameSelectedTitle()
    {
        if (_selectedSession == null) return;

        var currentTitle = _selectedSession.Title ?? string.Empty;
        var newTitle = Microsoft.VisualBasic.Interaction.InputBox("Enter new session title:", "Rename Recording Title", currentTitle);
        if (string.IsNullOrWhiteSpace(newTitle)) return;

        _selectedSession.Title = newTitle.Trim();
        _sessionStore.Save(_selectedSession);
        UpdateDetailPane();
        FilterSessions();
    }

    private void ToggleProtectSelected()
    {
        if (_selectedSession == null) return;

        _selectedSession.Protected = !_selectedSession.Protected;
        _sessionStore.Save(_selectedSession);
        UpdateDetailPane();
        FilterSessions();
    }

    private void DeleteSelectedSession()
    {
        if (_selectedSession == null) return;

        if (_selectedSession.Protected)
        {
            MessageBox.Show(this, "This recording is protected. Unprotect it first to delete.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Collect files to delete
        var filesToDelete = new List<string>();
        if (!string.IsNullOrEmpty(_selectedSession.MergedPath))
        {
            var expMerged = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_selectedSession.MergedPath));
            if (File.Exists(expMerged)) filesToDelete.Add(expMerged);
        }
        foreach (var seg in _selectedSession.Segments)
        {
            var expLocation = Path.GetFullPath(Environment.ExpandEnvironmentVariables(seg.Location));
            if (!string.IsNullOrEmpty(seg.FinalPath))
            {
                var fullFinal = Path.IsPathRooted(seg.FinalPath)
                    ? seg.FinalPath
                    : Path.GetFullPath(Path.Combine(expLocation, seg.FinalPath));
                if (File.Exists(fullFinal)) filesToDelete.Add(fullFinal);
            }
            if (!string.IsNullOrEmpty(seg.TsPath))
            {
                var fullTs = Path.IsPathRooted(seg.TsPath)
                    ? seg.TsPath
                    : Path.GetFullPath(Path.Combine(expLocation, seg.TsPath));
                if (File.Exists(fullTs)) filesToDelete.Add(fullTs);
            }
        }

        var totalBytes = filesToDelete.Sum(f => new FileInfo(f).Length);
        var mb = totalBytes / (1024.0 * 1024.0);

        var confirm = MessageBox.Show(this,
            $"Are you sure you want to delete this recording?\n\nFiles: {filesToDelete.Count}\nTotal Size: {mb:F1} MB\n\nFiles will be moved to the Windows Recycle Bin.",
            "Confirm Delete Recording",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        try
        {
            foreach (var file in filesToDelete)
            {
                try
                {
                    FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not recycle file {File}", file);
                }
            }

            // Remove canonical session manifest and any backup copies
            var fs = new System.IO.Abstractions.FileSystem();
            var mirrorDirs = _selectedSession.Segments
                .Select(s => StorageDirectoryHelper.GetMetadataDirectory(fs, s.Location))
                .Distinct()
                .ToList();
            _sessionStore.Delete(_selectedSession.SessionId, mirrorDirs);

            _allSessions.Remove(_selectedSession);
            _selectedSession = null;
            FilterSessions();
            MessageBox.Show(this, "Recording files moved to Recycle Bin.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to delete recording: {ex.Message}", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportMarkersForSelected()
    {
        if (_selectedSession == null || _selectedSession.Markers.Count == 0)
        {
            MessageBox.Show(this, "The selected session has no markers to export.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Export Markers",
            Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
            FileName = $"markers_{_selectedSession.SessionId}.txt"
        };

        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var marker in _selectedSession.Markers.OrderBy(m => m.OffsetSec))
                {
                    var localTime = marker.AtUtc.ToLocalTime();
                    var tag = string.Equals(marker.Kind, "User", StringComparison.OrdinalIgnoreCase) ? "marker" : marker.Kind.ToLowerInvariant();
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{localTime:HH:mm:ss}  [{tag}]  {marker.Note}");
                }
                File.WriteAllText(sfd.FileName, sb.ToString());
                MessageBox.Show(this, "Markers successfully exported.", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to export markers: {ex.Message}", "ScreenVault", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
