using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using ScreenVault.App.Platform;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Serilog;

namespace ScreenVault.App.UI;

/// <summary>Browse past sessions: searchable list on the left, details and actions on the right.</summary>
public sealed class LibraryForm : ModernForm
{
    private const int DetailWidth = 376;

    private readonly ISessionStore _sessionStore;
    private readonly IRecordingController _controller;
    private readonly ISettingsService _settingsService;
    private readonly PlayerLauncher _playerLauncher;
    private readonly IClipExporter _clipExporter;
    private readonly ToolTip _toolTip = ModernToolTip.Create();

    private readonly TextField _txtSearch;
    private readonly ModernButton _btnRefresh;
    private readonly TextLabel _lblCount;
    private readonly ThemedListView _lstSessions;

    // Detail pane
    private readonly CardPanel _detailCard;
    private readonly Panel _emptyState;
    private readonly Panel _detailContent;
    private readonly TextLabel _lblDetailTitle;
    private readonly TextLabel _lblDetailWhen;
    private readonly TextLabel _lblDetailMeta;
    private readonly TextLabel _valDuration;
    private readonly TextLabel _valSize;
    private readonly TextLabel _valParts;
    private readonly TextLabel _valMarkers;
    private readonly SegmentedControl _tabs;
    private readonly ThemedListView _lstMarkers;
    private readonly ThemedListView _lstParts;
    private readonly ThemedListView _lstEvents;
    private readonly ModernButton _btnExportMarkers;

    private readonly ModernButton _btnPlay;
    private readonly ModernButton _btnShowInFolder;
    private readonly ModernButton _btnExportClip;
    private readonly ModernButton _btnMerge;
    private readonly ModernButton _btnRename;
    private readonly ModernButton _btnProtect;
    private readonly ModernButton _btnDelete;

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

        Text = "ScreenVault – Recordings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 720);
        MinimumSize = new Size(900, 560);
        KeyPreview = true;

        // ── Header ───────────────────────────────────────────────────────────────────
        var header = new SurfacePanel { Size = new Size(1120, 84), Dock = DockStyle.Top };
        header.Controls.Add(new TextLabel("Recordings", Typography.Display) { Location = new Point(26, 16) });
        _lblCount = new TextLabel("Loading recordings…", Typography.Caption, TextTone.Secondary) { Location = new Point(28, 54) };
        _txtSearch = new TextField
        {
            Bounds = new Rectangle(704, 26, 340, 34),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            LeadingGlyph = Glyphs.Search,
            PlaceholderText = "Search titles, markers or session IDs"
        };
        _txtSearch.TextChanged += (_, _) => FilterSessions();
        _btnRefresh = new ModernButton(string.Empty, ButtonKind.Secondary, Glyphs.Refresh)
        {
            Bounds = new Rectangle(1054, 26, 40, 34),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AccessibleName = "Refresh"
        };
        _btnRefresh.Click += async (_, _) => await LoadSessionsAsync();
        _toolTip.SetToolTip(_btnRefresh, "Reload recordings from disk");
        header.Controls.AddRange([_lblCount, _txtSearch, _btnRefresh]);

        // ── Sessions list ────────────────────────────────────────────────────────────
        var listHost = new SurfacePanel { Size = new Size(720, 636), Dock = DockStyle.Fill, Padding = new Padding(24, 0, 12, 24) };
        var listCard = new CardPanel { ManualLayout = true, Dock = DockStyle.Fill, Padding = new Padding(1, 6, 1, 6) };
        _lstSessions = new ThemedListView { Dock = DockStyle.Fill, RowHeight = 40, EmptyText = "No recordings yet. Start a recording and it will appear here." };
        _lstSessions.Columns.Add("Title", 250);
        _lstSessions.Columns.Add("Date", 140);
        _lstSessions.Columns.Add("Duration", 84, HorizontalAlignment.Right);
        _lstSessions.Columns.Add("Size", 84, HorizontalAlignment.Right);
        _lstSessions.Columns.Add("Parts", 56, HorizontalAlignment.Right);
        _lstSessions.Columns.Add("Markers", 70, HorizontalAlignment.Right);
        _lstSessions.Columns.Add("Status", 120);
        _lstSessions.CellPainter = PaintSessionCell;
        _lstSessions.SelectedIndexChanged += (_, _) => OnSessionSelectionChanged();
        _lstSessions.DoubleClick += (_, _) => PlaySelectedSession();
        _lstSessions.KeyDown += OnSessionListKeyDown;
        listCard.Controls.Add(_lstSessions);
        listHost.Controls.Add(listCard);

        // ── Detail pane ──────────────────────────────────────────────────────────────
        var detailHost = new SurfacePanel { Size = new Size(DetailWidth + 36, 636), Dock = DockStyle.Right, Padding = new Padding(12, 0, 24, 24) };
        _detailCard = new CardPanel { ManualLayout = true, Dock = DockStyle.Fill, Size = new Size(DetailWidth, 612), Padding = new Padding(1) };

        _emptyState = new Panel { Dock = DockStyle.Fill };
        _emptyState.Controls.Add(new GlyphBadge { Glyph = Glyphs.Library, Tone = Tone.Accent, Bounds = new Rectangle((DetailWidth - 56) / 2, 220, 56, 56), Anchor = AnchorStyles.Top });
        _emptyState.Controls.Add(new TextLabel("Select a recording", Typography.Subtitle)
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, 288, DetailWidth, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        });
        _emptyState.Controls.Add(new TextLabel("Its details, markers and actions appear here.", Typography.Caption, TextTone.Secondary)
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, 314, DetailWidth, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        });

        _detailContent = new Panel { Dock = DockStyle.Fill, Size = new Size(DetailWidth, 612), Visible = false };
        const int inner = DetailWidth - 40;
        _lblDetailTitle = new TextLabel(string.Empty, Typography.Title) { AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(20, 18, inner, 28), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _lblDetailWhen = new TextLabel(string.Empty, Typography.Body, TextTone.Secondary) { AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(20, 48, inner, 20), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _lblDetailMeta = new TextLabel(string.Empty, Typography.Caption, TextTone.Tertiary) { AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(20, 70, inner, 18), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

        const int tileWidth = (inner - 24) / 4;
        _valDuration = StatTile(_detailContent, "Duration", 20, 98, tileWidth);
        _valSize = StatTile(_detailContent, "Size", 20 + (tileWidth + 8), 98, tileWidth);
        _valParts = StatTile(_detailContent, "Parts", 20 + (2 * (tileWidth + 8)), 98, tileWidth);
        _valMarkers = StatTile(_detailContent, "Markers", 20 + (3 * (tileWidth + 8)), 98, tileWidth);

        _btnPlay = new ModernButton("Play recording", ButtonKind.Primary, Glyphs.Play) { Bounds = new Rectangle(20, 162, inner, 38), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _btnPlay.Click += (_, _) => PlaySelectedSession();

        var actions = new FlowLayoutPanel
        {
            Bounds = new Rectangle(16, 208, inner + 8, 112),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        _btnShowInFolder = ActionButton(actions, "Show in folder", Glyphs.FolderOpen, ButtonKind.Subtle, "Show the file in Explorer (Ctrl+O)");
        _btnShowInFolder.Click += (_, _) => ShowSelectedInFolder();
        _btnExportClip = ActionButton(actions, "Export clip…", Glyphs.Cut, ButtonKind.Subtle, "Save part of this recording as a new file");
        _btnExportClip.Click += (_, _) => ExportClipForSelected();
        _btnMerge = ActionButton(actions, "Merge parts", Glyphs.Merge, ButtonKind.Subtle, "Join all parts into one video (lossless)");
        _btnMerge.Click += async (_, _) => await MergeSelectedSessionAsync();
        _btnRename = ActionButton(actions, "Rename…", Glyphs.Rename, ButtonKind.Subtle, "Give this recording a title (F2)");
        _btnRename.Click += (_, _) => RenameSelectedTitle();
        _btnProtect = ActionButton(actions, "Protect", Glyphs.Lock, ButtonKind.Subtle, "Protected recordings are never deleted by automatic clean-up");
        _btnProtect.Click += (_, _) => ToggleProtectSelected();
        _btnDelete = ActionButton(actions, "Delete", Glyphs.Delete, ButtonKind.Destructive, "Move this recording to the Recycle Bin (Del)");
        _btnDelete.Click += (_, _) => DeleteSelectedSession();

        _tabs = new SegmentedControl { Bounds = new Rectangle(20, 326, inner, 34), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Recording details" };
        _tabs.AddItem("Markers");
        _tabs.AddItem("Parts");
        _tabs.AddItem("Timeline");
        _tabs.SelectedIndexChanged += (_, _) => ShowDetailTab(_tabs.SelectedIndex);

        var listBounds = new Rectangle(12, 368, inner + 16, 196);
        const AnchorStyles fill = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _lstMarkers = new ThemedListView { Bounds = listBounds, Anchor = fill, RowHeight = 32, EmptyText = "No markers in this recording." };
        _lstMarkers.Columns.Add("Time", 76);
        _lstMarkers.Columns.Add("Note", 180);
        _lstMarkers.Columns.Add("Kind", 70);
        _lstMarkers.DoubleClick += (_, _) => JumpToSelectedMarker();
        _toolTip.SetToolTip(_lstMarkers, "Double-click a marker to play from that moment");

        _lstParts = new ThemedListView { Bounds = listBounds, Anchor = fill, RowHeight = 32, Visible = false, EmptyText = "No parts." };
        _lstParts.Columns.Add("Part", 56);
        _lstParts.Columns.Add("Size", 76, HorizontalAlignment.Right);
        _lstParts.Columns.Add("Length", 70, HorizontalAlignment.Right);
        _lstParts.Columns.Add("Status", 80);
        _lstParts.Columns.Add("File", 140);

        _lstEvents = new ThemedListView { Bounds = listBounds, Anchor = fill, RowHeight = 32, Visible = false, EmptyText = "No events were logged." };
        _lstEvents.Columns.Add("Time", 76);
        _lstEvents.Columns.Add("Event", 110);
        _lstEvents.Columns.Add("Detail", 180);

        _btnExportMarkers = new ModernButton("Export markers…", ButtonKind.Subtle, Glyphs.Export)
        {
            Bounds = new Rectangle(16, 568, 150, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        _btnExportMarkers.Click += (_, _) => ExportMarkersForSelected();
        _toolTip.SetToolTip(_btnExportMarkers, "Save the marker list as a text file");

        _detailContent.Controls.AddRange([_lblDetailTitle, _lblDetailWhen, _lblDetailMeta, _btnPlay, actions, _tabs, _lstMarkers, _lstParts, _lstEvents, _btnExportMarkers]);
        _detailCard.Controls.Add(_detailContent);
        _detailCard.Controls.Add(_emptyState);
        detailHost.Controls.Add(_detailCard);

        Controls.Add(listHost);
        Controls.Add(detailHost);
        Controls.Add(header);

        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.O && _btnShowInFolder.Enabled)
            {
                ShowSelectedInFolder();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.F)
            {
                _txtSearch.Focus();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F5)
            {
                _ = LoadSessionsAsync();
                e.Handled = true;
            }
        };

        ResumeLayout(false);
        PerformLayout();

        Shown += async (_, _) => await LoadSessionsAsync();
    }

    public async Task LoadSessionsAsync()
    {
        _btnRefresh.Enabled = false;
        _lblCount.Text = "Loading recordings…";

        try
        {
            _allSessions = await Task.Run(() => _sessionStore.LoadAllCanonical().ToList()).ConfigureAwait(true);
            FilterSessions();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load sessions in LibraryForm");
            _lblCount.Text = "Could not load recordings.";
        }
        finally
        {
            _btnRefresh.Enabled = true;
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

    private static TextLabel StatTile(Control parent, string label, int left, int top, int width)
    {
        var tile = new CardPanel { ManualLayout = true, Bounds = new Rectangle(left, top, width, 52), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        tile.Surface = SurfaceKind.Window;
        tile.Controls.Add(new TextLabel(label, Typography.Caption, TextTone.Tertiary) { Location = new Point(12, 7) });
        var value = new TextLabel("—", Typography.Subtitle) { Location = new Point(12, 24) };
        tile.Controls.Add(value);
        parent.Controls.Add(tile);
        return value;
    }

    private ModernButton ActionButton(FlowLayoutPanel host, string text, char glyph, ButtonKind kind, string tooltip)
    {
        var button = new ModernButton(text, kind, glyph)
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 4, 4),
            Enabled = false
        };
        _toolTip.SetToolTip(button, tooltip);
        host.Controls.Add(button);
        return button;
    }

    private void ShowDetailTab(int index)
    {
        _lstMarkers.Visible = index == 0;
        _lstParts.Visible = index == 1;
        _lstEvents.Visible = index == 2;
        _btnExportMarkers.Visible = index == 0;
        var bottomPadding = index == 0 ? LogicalToDeviceUnits(48) : LogicalToDeviceUnits(12);
        foreach (var list in new[] { _lstMarkers, _lstParts, _lstEvents })
        {
            list.Height = Math.Max(LogicalToDeviceUnits(80), _detailContent.ClientSize.Height - list.Top - bottomPadding);
        }
    }

    private void OnSessionListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter when _selectedSession != null:
                PlaySelectedSession();
                e.Handled = true;
                break;
            case Keys.F2 when _selectedSession != null:
                RenameSelectedTitle();
                e.Handled = true;
                break;
            case Keys.Delete when _btnDelete.Enabled:
                DeleteSelectedSession();
                e.Handled = true;
                break;
        }
    }

    private void FilterSessions()
    {
        var previousId = _selectedSession?.SessionId;
        _lstSessions.BeginUpdate();
        _lstSessions.Items.Clear();

        var query = _txtSearch.Text.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _allSessions
            : _allSessions.Where(s =>
                (s.Title != null && s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                s.SessionId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Markers.Any(m => m.Note.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();

        ListViewItem? toSelect = null;
        foreach (var session in matches)
        {
            var item = new ListViewItem(DisplayTitle(session)) { Tag = session };
            item.SubItems.Add(session.StartedAtUtc.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture));
            item.SubItems.Add(FormatDuration(SessionDuration(session)));
            item.SubItems.Add(FormatSize(session.Segments.Sum(seg => seg.Bytes)));
            item.SubItems.Add(session.Segments.Count.ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(session.Markers.Count.ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(session.Status);
            _lstSessions.Items.Add(item);
            if (previousId != null && string.Equals(session.SessionId, previousId, StringComparison.Ordinal))
            {
                toSelect = item;
            }
        }

        _lstSessions.EndUpdate();
        _lblCount.Text = matches.Count == _allSessions.Count
            ? $"{matches.Count} recording{(matches.Count == 1 ? string.Empty : "s")}"
            : $"{matches.Count} of {_allSessions.Count} recordings match \"{query}\"";

        toSelect ??= _lstSessions.Items.Count > 0 ? _lstSessions.Items[0] : null;
        if (toSelect != null)
        {
            toSelect.Selected = true;
            toSelect.Focused = true;
            toSelect.EnsureVisible();
        }
        else
        {
            _selectedSession = null;
            UpdateDetailPane();
        }
    }

    private bool PaintSessionCell(DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is not SessionManifest session)
        {
            return false;
        }

        var p = Theme.Current;
        var g = e.Graphics;
        var scale = Draw.Scale(_lstSessions);
        if (e.ColumnIndex == 0)
        {
            var padding = (int)(12 * scale);
            var lockWidth = session.Protected && Glyphs.Available ? (int)(22 * scale) : 0;
            var textRect = new Rectangle(e.Bounds.X + padding, e.Bounds.Y, Math.Max(0, e.Bounds.Width - padding - lockWidth - 4), e.Bounds.Height);
            TextRenderer.DrawText(g, e.SubItem?.Text ?? string.Empty, Typography.BodyStrong, textRect, p.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (lockWidth > 0)
            {
                Glyphs.Draw(g, Glyphs.Lock, new Rectangle(e.Bounds.Right - lockWidth - 4, e.Bounds.Y, lockWidth, e.Bounds.Height), p.AccentText, 9f);
            }

            return true;
        }

        if (e.ColumnIndex == 6)
        {
            var (text, tone) = session.Status switch
            {
                "Completed" => ("Completed", Tone.Success),
                "Interrupted" => ("Interrupted", Tone.Warning),
                "Recording" => ("Recording", Tone.Danger),
                _ => (session.Status, Tone.Neutral)
            };
            var textWidth = TextRenderer.MeasureText(text, Typography.CaptionStrong).Width;
            var pill = new RectangleF(e.Bounds.X + (10 * scale), e.Bounds.Y + ((e.Bounds.Height - (22 * scale)) / 2f), textWidth + (16 * scale), 22 * scale);
            Draw.PrepareHighQuality(g);
            Draw.FillRounded(g, p.Soft(tone), pill, pill.Height / 2f);
            TextRenderer.DrawText(g, text, Typography.CaptionStrong, Rectangle.Round(pill), p.Foreground(tone),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return true;
        }

        return false;
    }

    private void OnSessionSelectionChanged()
    {
        _selectedSession = _lstSessions.SelectedItems.Count > 0 && _lstSessions.SelectedItems[0].Tag is SessionManifest session
            ? session
            : null;

        UpdateDetailPane();
    }

    private void UpdateDetailPane()
    {
        var hasSelection = _selectedSession != null;
        _detailContent.Visible = hasSelection;
        _emptyState.Visible = !hasSelection;
        foreach (var button in new[] { _btnPlay, _btnShowInFolder, _btnExportClip, _btnMerge, _btnRename, _btnProtect, _btnDelete })
        {
            button.Enabled = hasSelection;
        }

        if (_selectedSession == null)
        {
            _lstMarkers.Items.Clear();
            _lstParts.Items.Clear();
            _lstEvents.Items.Clear();
            return;
        }

        var s = _selectedSession;
        _lblDetailTitle.Text = DisplayTitle(s);
        _toolTip.SetToolTip(_lblDetailTitle, DisplayTitle(s));
        var started = s.StartedAtUtc.ToLocalTime();
        _lblDetailWhen.Text = s.EndedAtUtc.HasValue
            ? $"{started.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture)} · {started:HH:mm}–{s.EndedAtUtc.Value.ToLocalTime():HH:mm}"
            : $"{started.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture)} · started {started:HH:mm}";
        _lblDetailMeta.Text = $"{s.SessionId} · {s.Machine} · {s.Video.EncoderProfile} · {s.Video.Fps} fps";
        _toolTip.SetToolTip(_lblDetailMeta, $"Session ID: {s.SessionId}{Environment.NewLine}Machine: {s.Machine}{Environment.NewLine}Encoder: {s.Video.EncoderProfile} ({s.Video.Fps} fps)");

        _valDuration.Text = FormatDuration(SessionDuration(s));
        _valSize.Text = FormatSize(s.Segments.Sum(seg => seg.Bytes));
        _valParts.Text = s.Segments.Count.ToString(CultureInfo.CurrentCulture);
        _valMarkers.Text = s.Markers.Count.ToString(CultureInfo.CurrentCulture);

        _btnMerge.Enabled = s.Segments.Count > 1 && !MergedFileExists(s);
        _btnProtect.Text = s.Protected ? "Unprotect" : "Protect";
        _btnProtect.Glyph = s.Protected ? Glyphs.Unlock : Glyphs.Lock;

        // Delete allowed only if not the active recording session
        var isActive = IsBeingRecorded(s);
        _btnDelete.Enabled = !isActive;
        _toolTip.SetToolTip(_btnDelete, isActive ? "This session is still being recorded" : "Move this recording to the Recycle Bin (Del)");

        _lstMarkers.BeginUpdate();
        _lstMarkers.Items.Clear();
        foreach (var m in s.Markers)
        {
            var item = new ListViewItem(TimeSpan.FromSeconds(m.OffsetSec).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)) { Tag = m };
            item.SubItems.Add(string.IsNullOrWhiteSpace(m.Note) ? "(no note)" : m.Note);
            item.SubItems.Add(m.Kind);
            _lstMarkers.Items.Add(item);
        }

        _lstMarkers.EndUpdate();

        _lstParts.BeginUpdate();
        _lstParts.Items.Clear();
        foreach (var seg in s.Segments)
        {
            var item = new ListViewItem(seg.Index.ToString(CultureInfo.CurrentCulture));
            item.SubItems.Add(FormatSize(seg.Bytes));
            item.SubItems.Add(seg.DurationSec.HasValue ? FormatDuration(TimeSpan.FromSeconds(seg.DurationSec.Value)) : "—");
            item.SubItems.Add(seg.Remux);
            item.SubItems.Add(Path.GetFileName(!string.IsNullOrEmpty(seg.FinalPath) ? seg.FinalPath : seg.TsPath));
            _lstParts.Items.Add(item);
        }

        _lstParts.EndUpdate();

        _lstEvents.BeginUpdate();
        _lstEvents.Items.Clear();
        foreach (var evt in s.Events)
        {
            var item = new ListViewItem(evt.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            item.SubItems.Add(evt.Type);
            item.SubItems.Add(evt.Detail);
            _lstEvents.Items.Add(item);
        }

        _lstEvents.EndUpdate();
        ShowDetailTab(_tabs.SelectedIndex);
    }

    private bool IsBeingRecorded(SessionManifest session) =>
        _controller.Desired != DesiredState.Stopped &&
        (string.Equals(session.Status, "Recording", StringComparison.OrdinalIgnoreCase) ||
         (_controller.Health.CurrentFilePath?.Contains(session.SessionId, StringComparison.OrdinalIgnoreCase) ?? false));

    private static bool MergedFileExists(SessionManifest session)
    {
        if (string.IsNullOrEmpty(session.MergedPath))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.GetFullPath(Environment.ExpandEnvironmentVariables(session.MergedPath)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Saves a change atomically and mirrors it into the copy shown in the list.</summary>
    private bool UpdateSession(SessionManifest session, Action<SessionManifest> change)
    {
        try
        {
            _sessionStore.Update(session.SessionId, m =>
            {
                change(m);
                return true;
            });
            change(session);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not update session {SessionId}", session.SessionId);
            ModernDialog.Error(this, "Could not save the change", ex.Message);
            return false;
        }
    }

    private static string DisplayTitle(SessionManifest session) =>
        string.IsNullOrWhiteSpace(session.Title) ? $"Recording {session.StartedAtUtc.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture)}" : session.Title;

    private static TimeSpan SessionDuration(SessionManifest session)
    {
        var seconds = session.Segments.Sum(seg => seg.DurationSec ?? 0.0);
        if (seconds <= 0 && session.EndedAtUtc.HasValue)
        {
            seconds = (session.EndedAtUtc.Value - session.StartedAtUtc).TotalSeconds;
        }

        return TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "—";
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatSize(long bytes)
    {
        var mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024
            ? string.Create(CultureInfo.CurrentCulture, $"{mb / 1024.0:F2} GB")
            : string.Create(CultureInfo.CurrentCulture, $"{mb:F1} MB");
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
            ModernDialog.Warning(this, "Nothing to play", "No video file was found for this recording. It may have been moved or deleted.");
            return;
        }

        _playerLauncher.Launch(file, owner: this);
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
                _playerLauncher.Launch(expMerged, targetSeek, this);
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
                        _playerLauncher.Launch(full, targetSeek, this);
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
            _playerLauncher.Launch(fallback, Math.Max(0.0, marker.OffsetSec - preRoll), this);
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
            var session = _selectedSession;
            var res = await merger.MergeSessionAsync(session.SessionId).ConfigureAwait(true);
            if (res.Success && !string.IsNullOrEmpty(res.MergedFilePath))
            {
                UpdateSession(session, m =>
                {
                    m.MergedPath = res.MergedFilePath;
                    m.MergeStatus = "Done";
                });
                ModernDialog.Success(this, "Parts merged", $"Saved as {Path.GetFileName(res.MergedFilePath)} in the same folder.");
                await LoadSessionsAsync();
            }
            else
            {
                ModernDialog.Error(this, "Merge failed", res.ErrorMessage ?? "The parts could not be merged.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Merge failed for session {SessionId}", _selectedSession?.SessionId);
            ModernDialog.Error(this, "Merge failed", ex.Message);
        }
        finally
        {
            _btnMerge.Text = "Merge parts";
            UpdateDetailPane();
        }
    }

    private void RenameSelectedTitle()
    {
        if (_selectedSession == null) return;

        var newTitle = ModernDialog.Prompt(this, "Rename recording", "Give this recording a title that's easy to search for.", _selectedSession.Title ?? string.Empty, "Rename", "e.g. Sprint planning");
        if (string.IsNullOrWhiteSpace(newTitle)) return;

        var title = newTitle.Trim();
        if (title.Length > 120) title = title[..120].TrimEnd();
        if (UpdateSession(_selectedSession, m => m.Title = title))
        {
            FilterSessions();
        }
    }

    private void ToggleProtectSelected()
    {
        if (_selectedSession == null) return;

        var protect = !_selectedSession.Protected;
        if (UpdateSession(_selectedSession, m => m.Protected = protect))
        {
            FilterSessions();
        }
    }

    private void DeleteSelectedSession()
    {
        if (_selectedSession == null) return;

        if (_selectedSession.Protected)
        {
            ModernDialog.Info(this, "This recording is protected", "Unprotect it first if you really want to delete it.");
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
                if (File.Exists(fullFinal) && !filesToDelete.Contains(fullFinal, StringComparer.OrdinalIgnoreCase)) filesToDelete.Add(fullFinal);
            }

            if (!string.IsNullOrEmpty(seg.TsPath))
            {
                var fullTs = Path.IsPathRooted(seg.TsPath)
                    ? seg.TsPath
                    : Path.GetFullPath(Path.Combine(expLocation, seg.TsPath));
                if (File.Exists(fullTs) && !filesToDelete.Contains(fullTs, StringComparer.OrdinalIgnoreCase)) filesToDelete.Add(fullTs);
            }
        }

        var totalBytes = filesToDelete.Sum(f =>
        {
            try
            {
                return new FileInfo(f).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0L;
            }
        });

        if (IsBeingRecorded(_selectedSession))
        {
            ModernDialog.Info(this, "This recording is still running", "Stop the recording first, then delete it.");
            return;
        }
        var confirmed = ModernDialog.Confirm(this,
            "Delete this recording?",
            $"{filesToDelete.Count} file{(filesToDelete.Count == 1 ? string.Empty : "s")} ({FormatSize(totalBytes)}) will be moved to the Recycle Bin, so you can still restore them.",
            "Delete",
            "Cancel",
            destructive: true,
            icon: MessageBoxIcon.Warning);

        if (!confirmed) return;

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
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete recording.");
            ModernDialog.Error(this, "Could not delete the recording", ex.Message);
        }
    }

    private void ExportMarkersForSelected()
    {
        if (_selectedSession == null || _selectedSession.Markers.Count == 0)
        {
            ModernDialog.Info(this, "No markers to export", "Add markers while recording with the marker button or its shortcut.");
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
                ModernDialog.Success(this, "Markers exported", $"Saved to {Path.GetFileName(sfd.FileName)}.");
            }
            catch (Exception ex)
            {
                ModernDialog.Error(this, "Could not export markers", ex.Message);
            }
        }
    }
}
