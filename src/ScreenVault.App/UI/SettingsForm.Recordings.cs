using System.Globalization;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private readonly ThemedListBox _lstLocations = new() { ItemHeightLogical = 56 };
    private readonly ModernButton _btnAddLocation = new("Add folder…", ButtonKind.Secondary, Glyphs.Add);
    private readonly ModernButton _btnRemoveLocation = new("Remove", ButtonKind.Subtle, Glyphs.Delete);
    private readonly ModernButton _btnMoveUp = new("Move up", ButtonKind.Subtle, Glyphs.ChevronUp);
    private readonly ModernButton _btnMoveDown = new("Move down", ButtonKind.Subtle, Glyphs.ChevronDown);

    private readonly ModernComboBox _cmbSplitMinutes = new();
    private readonly ModernComboBox _cmbSplitSize = new();
    private readonly ModernComboBox _cmbOutputFormat = new();
    private readonly ToggleSwitch _chkKeepTs = new();
    private readonly ToggleSwitch _chkFailback = new();
    private readonly ToggleSwitch _chkMetadataNextToRecordings = new();

    private readonly ToggleSwitch _chkRetention = new();
    private readonly NumberField _numRetentionDays = new() { Minimum = 1, Maximum = 365, Value = 30, Suffix = "days" };
    private readonly ToggleSwitch _chkProtectWithMarkers = new();

    private readonly ToggleSwitch _chkShowSavedDialog = new();
    private readonly ToggleSwitch _chkMergeOnSave = new();
    private readonly ToggleSwitch _chkDeletePartsAfterMerge = new();

    private readonly ModernComboBox _cmbPlayer = new();
    private readonly TextField _txtCustomPlayer = new() { PlaceholderText = "Select player executable..." };
    private readonly ModernButton _btnBrowsePlayer = new("Browse…", ButtonKind.Secondary, Glyphs.FolderOpen);
    private SettingRow _customPlayerRow = null!;
    private readonly NumberField _numMarkerPreRoll = new() { Minimum = 0, Maximum = 30, Value = 5, Suffix = "s" };

    private readonly List<int> _splitMinuteValues = [0, 5, 10, 15, 30, 60, 120];
    private readonly List<int> _splitSizeValues = [0, 500, 1000, 2000, 4000];
    private readonly List<PlaybackPlayer> _playerValues = [PlaybackPlayer.Auto, PlaybackPlayer.SystemDefault, PlaybackPlayer.Vlc, PlaybackPlayer.Ffplay, PlaybackPlayer.Custom];

    private StackPanel BuildRecordingsPage()
    {
        _cmbSplitMinutes.Items.AddRange(["Don't split by time", "5 minutes", "10 minutes (recommended)", "15 minutes", "30 minutes", "60 minutes", "120 minutes"]);
        _cmbSplitSize.Items.AddRange(["Off (no size limit)", "500 MB", "1 GB", "2 GB", "4 GB"]);
        _cmbOutputFormat.Items.AddRange(["MKV (recommended)", "MP4 (most compatible)", "TS (raw live format)"]);
        _cmbPlayer.Items.AddRange(["Auto-detect best player", "Windows default video player", "VLC media player", "FFplay", "Other app…"]);

        foreach (var combo in new[] { _cmbSplitMinutes, _cmbSplitSize, _cmbOutputFormat, _cmbPlayer })
        {
            combo.Width = 260;
        }

        _numRetentionDays.Width = 130;
        _numMarkerPreRoll.Width = 110;
        _btnBrowsePlayer.Size = new Size(100, 32);
        _btnBrowsePlayer.Click += (_, _) => BrowseCustomPlayer();

        _cmbPlayer.SelectedIndexChanged += (_, _) =>
        {
            var isCustom = ValueAt(_playerValues, _cmbPlayer.SelectedIndex, PlaybackPlayer.Auto) == PlaybackPlayer.Custom;
            if (_customPlayerRow != null) _customPlayerRow.Visible = isCustom;
        };

        var page = CreatePage("Recordings", "Storage locations, file splitting, formats, cleanup, and playback options.");

        page.Controls.Add(Section("Save locations"));
        page.Controls.Add(new TextLabel("ScreenVault saves to the first location that has enough free space and moves to the next one automatically if a drive fills up or disconnects.", Typography.Caption, TextTone.Secondary, wrap: true));
        _lstLocations.Height = 172;
        _lstLocations.ItemPainter = PaintLocationItem;
        _lstLocations.SelectedIndexChanged += (_, _) => UpdateLocationButtons();
        _btnAddLocation.Click += (_, _) => AddStorageLocation();
        _btnRemoveLocation.Click += (_, _) => RemoveStorageLocation();
        _btnMoveUp.Click += (_, _) => MoveStorageLocation(-1);
        _btnMoveDown.Click += (_, _) => MoveStorageLocation(1);

        var locationButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(10, 8, 10, 8)
        };
        foreach (var button in new[] { _btnAddLocation, _btnMoveUp, _btnMoveDown, _btnRemoveLocation })
        {
            button.AutoSize = true;
            button.Margin = new Padding(0, 0, 8, 0);
            locationButtons.Controls.Add(button);
        }

        var locationsCard = new CardPanel { Padding = new Padding(1, 6, 1, 0), Spacing = 0, Dividers = true };
        locationsCard.Controls.Add(_lstLocations);
        locationsCard.Controls.Add(locationButtons);
        page.Controls.Add(locationsCard);

        page.Controls.Add(Section("Files & splitting"));
        page.Controls.Add(Card(
            Row("Split recordings every", "Shorter parts limit data at risk if a crash occurs. Choose 'Don't split' for single files.", _cmbSplitMinutes, Glyphs.Cut),
            Row("Also split at file size", "Optionally split files when they reach a certain size.", _cmbSplitSize, Glyphs.HardDrive),
            Row("File format", "MKV is the most robust against crashes. MP4 plays everywhere.", _cmbOutputFormat, Glyphs.Video),
            Row("Keep raw .ts files", "Keep original live transport stream files after remuxing.", _chkKeepTs),
            Row("Return to primary drive", "Switch back once primary drive has enough free space again.", _chkFailback),
            Row("Save session details next to recordings", "Saves .manifest.json metadata sidecar file with markers and chapters.", _chkMetadataNextToRecordings, Glyphs.Info)));

        page.Controls.Add(Section("Clean-up"));
        _chkRetention.CheckedChanged += (_, _) => _numRetentionDays.Enabled = _chkRetention.Checked;
        page.Controls.Add(Card(
            Row("Delete old recordings automatically", "Deletes recordings older than the retention threshold.", _chkRetention, Glyphs.Delete),
            Row("Keep recordings for", null, _numRetentionDays),
            Row("Never delete recordings with markers", "Protects sessions that contain bookmarks/markers from automatic deletion.", _chkProtectWithMarkers, Glyphs.Flag)));

        page.Controls.Add(Section("When a recording stops"));
        _chkMergeOnSave.CheckedChanged += (_, _) => _chkDeletePartsAfterMerge.Enabled = _chkMergeOnSave.Checked;
        page.Controls.Add(Card(
            Row("Show \"Recording saved\" window", "Rename, play or copy the recording right after you stop.", _chkShowSavedDialog, Glyphs.Completed),
            Row("Merge parts into one file", "Joins multiple segment files of a session without re-encoding.", _chkMergeOnSave, Glyphs.Merge),
            Row("Delete parts after merging", "Only after the merged file has been verified.", _chkDeletePartsAfterMerge)));

        page.Controls.Add(Section("Playback"));
        var customPlayerPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _txtCustomPlayer.Width = 220;
        customPlayerPanel.Controls.Add(_txtCustomPlayer);
        customPlayerPanel.Controls.Add(_btnBrowsePlayer);
        _customPlayerRow = Row("Custom player executable", null, customPlayerPanel, Glyphs.OpenWith);

        page.Controls.Add(Card(
            Row("Open recordings with", "Select which media player to use when previewing recordings.", _cmbPlayer, Glyphs.Play),
            _customPlayerRow,
            Row("Marker jump pre-roll", "Rewind seconds when jumping to a marker in recordings.", _numMarkerPreRoll, Glyphs.Clock)));

        return page;
    }

    private void LoadRecordingsSettings(AppSettings s)
    {
        RefreshLocationList(selectIndex: 0);

        _cmbSplitMinutes.SelectedIndex = SelectValue(_cmbSplitMinutes, _splitMinuteValues, s.Storage.SplitMinutes,
            minutes => minutes == 0 ? "Don't split by time" : $"{minutes} minutes (current)");

        _cmbSplitSize.SelectedIndex = SelectValue(_cmbSplitSize, _splitSizeValues, s.Storage.SplitSizeMb,
            mb => mb == 0 ? "Off" : $"{mb} MB");

        _cmbOutputFormat.SelectedIndex = s.Storage.OutputFormat switch
        {
            OutputContainerFormat.Mp4 => 1,
            OutputContainerFormat.Ts => 2,
            _ => 0
        };

        _chkKeepTs.Checked = s.Storage.KeepTsAfterRemux;
        _chkFailback.Checked = s.Storage.FailbackToPrimary;
        _chkMetadataNextToRecordings.Checked = s.Storage.MetadataNextToRecordings;

        _chkRetention.Checked = s.Storage.Retention.Enabled;
        _numRetentionDays.Value = Math.Clamp(s.Storage.Retention.KeepDays, 1, 365);
        _numRetentionDays.Enabled = _chkRetention.Checked;
        _chkProtectWithMarkers.Checked = s.Storage.Retention.ProtectSessionsWithMarkers;

        _chkShowSavedDialog.Checked = s.Saving.ShowSavedDialog;
        _chkMergeOnSave.Checked = s.Saving.MergeOnSave;
        _chkDeletePartsAfterMerge.Checked = s.Saving.DeletePartsAfterMerge;
        _chkDeletePartsAfterMerge.Enabled = _chkMergeOnSave.Checked;

        _cmbPlayer.SelectedIndex = SelectValue(_cmbPlayer, _playerValues, s.Playback.Player,
            p => p.ToString());
        _txtCustomPlayer.Text = s.Playback.CustomPlayerPath ?? string.Empty;
        if (_customPlayerRow != null)
            _customPlayerRow.Visible = s.Playback.Player == PlaybackPlayer.Custom;

        _numMarkerPreRoll.Value = Math.Clamp(s.Playback.MarkerPreRollSec, 0, 30);
    }

    private void SaveRecordingsSettings(AppSettings s)
    {
        s.Storage.SplitMinutes = ValueAt(_splitMinuteValues, _cmbSplitMinutes.SelectedIndex, 10);
        s.Storage.SplitSizeMb = ValueAt(_splitSizeValues, _cmbSplitSize.SelectedIndex, 0);
        s.Storage.OutputFormat = _cmbOutputFormat.SelectedIndex switch
        {
            1 => OutputContainerFormat.Mp4,
            2 => OutputContainerFormat.Ts,
            _ => OutputContainerFormat.Mkv
        };
        s.Storage.KeepTsAfterRemux = _chkKeepTs.Checked;
        s.Storage.FailbackToPrimary = _chkFailback.Checked;
        s.Storage.MetadataNextToRecordings = _chkMetadataNextToRecordings.Checked;

        s.Storage.Retention.Enabled = _chkRetention.Checked;
        s.Storage.Retention.KeepDays = (int)_numRetentionDays.Value;
        s.Storage.Retention.ProtectSessionsWithMarkers = _chkProtectWithMarkers.Checked;

        s.Saving.ShowSavedDialog = _chkShowSavedDialog.Checked;
        s.Saving.MergeOnSave = _chkMergeOnSave.Checked;
        s.Saving.DeletePartsAfterMerge = _chkDeletePartsAfterMerge.Checked;

        s.Playback.Player = ValueAt(_playerValues, _cmbPlayer.SelectedIndex, PlaybackPlayer.Auto);
        s.Playback.CustomPlayerPath = string.IsNullOrWhiteSpace(_txtCustomPlayer.Text) ? null : _txtCustomPlayer.Text.Trim();
        s.Playback.MarkerPreRollSec = (int)_numMarkerPreRoll.Value;
    }

    private void BrowseCustomPlayer()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select Media Player Executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _txtCustomPlayer.Text = dlg.FileName;
        }
    }

    private void RefreshLocationList(int selectIndex)
    {
        _lstLocations.Items.Clear();
        foreach (var loc in _workingCopy.Storage.Locations)
        {
            _lstLocations.Items.Add(loc);
        }

        if (_lstLocations.Items.Count > 0)
        {
            _lstLocations.SelectedIndex = Math.Clamp(selectIndex, 0, _lstLocations.Items.Count - 1);
        }

        UpdateLocationButtons();
    }

    private void UpdateLocationButtons()
    {
        var idx = _lstLocations.SelectedIndex;
        var hasSel = idx >= 0;
        _btnRemoveLocation.Enabled = hasSel && _lstLocations.Items.Count > 1;
        _btnMoveUp.Enabled = hasSel && idx > 0;
        _btnMoveDown.Enabled = hasSel && idx < _lstLocations.Items.Count - 1;
    }

    private void PaintLocationItem(DrawItemEventArgs e, object item)
    {
        if (item is not StorageLocationConfig loc) return;

        var p = Theme.Current;
        var bounds = e.Bounds;
        var padding = LogicalToDeviceUnits(12);

        var pathText = loc.Path;
        var statusText = loc.Enabled ? $"Min free: {loc.MinFreeGb} GB" : "Disabled";

        TextRenderer.DrawText(e.Graphics, pathText, Typography.BodyStrong,
            new Rectangle(bounds.X + padding, bounds.Y + 8, bounds.Width - (padding * 2), 20),
            loc.Enabled ? p.Text : p.TextTertiary,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(e.Graphics, statusText, Typography.Caption,
            new Rectangle(bounds.X + padding, bounds.Y + 28, bounds.Width - (padding * 2), 18),
            p.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private void AddStorageLocation()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a folder for recordings" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _workingCopy.Storage.Locations.Add(new StorageLocationConfig
            {
                Path = dlg.SelectedPath,
                MinFreeGb = 5,
                Enabled = true
            });
            RefreshLocationList(_workingCopy.Storage.Locations.Count - 1);
        }
    }

    private void RemoveStorageLocation()
    {
        var idx = _lstLocations.SelectedIndex;
        if (idx >= 0 && _workingCopy.Storage.Locations.Count > 1)
        {
            _workingCopy.Storage.Locations.RemoveAt(idx);
            RefreshLocationList(Math.Clamp(idx, 0, _workingCopy.Storage.Locations.Count - 1));
        }
    }

    private void MoveStorageLocation(int delta)
    {
        var idx = _lstLocations.SelectedIndex;
        var newIdx = idx + delta;
        if (idx >= 0 && newIdx >= 0 && newIdx < _workingCopy.Storage.Locations.Count)
        {
            var item = _workingCopy.Storage.Locations[idx];
            _workingCopy.Storage.Locations.RemoveAt(idx);
            _workingCopy.Storage.Locations.Insert(newIdx, item);
            RefreshLocationList(newIdx);
        }
    }
}
