using System.Diagnostics;
using System.Globalization;
using System.IO;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;

namespace ScreenVault.App.UI;

public static class TrayMenuBuilder
{
    public static ContextMenuStrip Build(
        IRecordingController controller,
        IAudioEngine audioEngine,
        IStorageManager? storageManager,
        ISessionStore sessionStore,
        ISettingsService settingsService,
        IMarkerService markerService,
        Action showStatusAction,
        Action showSettingsAction,
        Action showLibraryAction,
        Action exitAction,
        IWin32Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(audioEngine);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(markerService);

        var menu = new ContextMenuStrip();
        ModernMenu.Apply(menu);
        var health = controller.Health;
        var settings = settingsService.Current;
        var hotkeys = settings.Hotkeys;
        var palette = Theme.Current;

        // 1. Status header: state + elapsed, then where we are saving
        var (stateText, dotColor) = health.State switch
        {
            RecorderState.Recording => (health.IsDegraded
                ? $"Recording · {Elapsed(health)} · needs attention"
                : $"Recording · {Elapsed(health)}", health.IsDegraded ? palette.Warning : palette.Danger),
            RecorderState.Paused => ($"Paused · {Elapsed(health)}", palette.Warning),
            RecorderState.Saving => ("Saving…", palette.TextTertiary),
            RecorderState.Faulted => ("Not recording — retrying", palette.Danger),
            RecorderState.Starting or RecorderState.Recovering => ("Starting…", palette.Accent),
            _ => ("Ready to record", palette.Success)
        };
        menu.Items.Add(new MenuHeaderItem(stateText, emphasized: true, dotColor: dotColor));

        // Where the next part goes: the active location, or the primary one before the first recording.
        var storageStatus = storageManager?.GetStatus();
        var activePath = storageStatus?.ActiveLocationPath ?? string.Empty;
        var activeLoc = storageStatus?.Locations.FirstOrDefault(l => string.Equals(l.ExpandedPath, activePath, StringComparison.OrdinalIgnoreCase))
                        ?? storageStatus?.Locations.FirstOrDefault(l => l.Enabled);
        var locDisplay = activeLoc?.ExpandedPath
                         ?? Environment.ExpandEnvironmentVariables(settings.Storage.Locations.FirstOrDefault(l => l.Enabled)?.Path ?? "default folder");
        var freeText = activeLoc is { AvailableFreeBytes: > 0 } ? $" · {StorageMeterList.FormatBytes(activeLoc.AvailableFreeBytes)} free" : string.Empty;
        menu.Items.Add(new MenuHeaderItem($"Saving to {Shorten(locDisplay)}{freeText}"));
        menu.Items.Add(new ToolStripSeparator());

        // 2. Primary action: Start recording / Stop & save (also while starting or retrying)
        if (health.Desired != DesiredState.Stopped)
        {
            var stopItem = ModernMenu.Item("Stop && save", Glyphs.Stop, async (_, _) =>
            {
                if (controller.State is RecorderState.Recording or RecorderState.Paused &&
                    !RecordingPrompts.ConfirmStop(owner, settingsService))
                {
                    return;
                }

                await controller.StopAsync().ConfigureAwait(true);
            }, HotkeyField.DisplayText(hotkeys.StartStop), palette.Danger);
            stopItem.Font = Typography.BodyStrong;
            menu.Items.Add(stopItem);
        }
        else
        {
            var startItem = ModernMenu.Item("Start recording", Glyphs.Record, async (_, _) =>
            {
                await controller.StartAsync().ConfigureAwait(true);
            }, HotkeyField.DisplayText(hotkeys.StartStop), palette.Danger);
            startItem.Font = Typography.BodyStrong;
            menu.Items.Add(startItem);
        }

        // 3. Pause / Resume
        if (health.Desired == DesiredState.Paused)
        {
            menu.Items.Add(ModernMenu.Item("Resume", Glyphs.Play, async (_, _) =>
            {
                await controller.ResumeAsync().ConfigureAwait(true);
            }, HotkeyField.DisplayText(hotkeys.PauseResume)));
        }
        else
        {
            var pauseItem = ModernMenu.Item("Pause", Glyphs.Pause, async (_, _) =>
            {
                await controller.PauseAsync().ConfigureAwait(true);
            }, HotkeyField.DisplayText(hotkeys.PauseResume));
            pauseItem.Enabled = health.State == RecorderState.Recording;
            menu.Items.Add(pauseItem);
        }

        // 4. Add marker
        var addMarkerItem = ModernMenu.Item("Add marker…", Glyphs.Flag, (_, _) =>
        {
            var timeStr = health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            using var dlg = new MarkerNoteForm(timeStr);
            // Owning it to the status flyout (when one is available) keeps it from opening behind
            // that always-topmost window.
            if (dlg.ShowDialog(owner) == DialogResult.OK)
            {
                markerService.AddMarker(dlg.NoteText, "User");
            }
        }, HotkeyField.DisplayText(hotkeys.AddMarker));
        addMarkerItem.Enabled = health.State is RecorderState.Recording or RecorderState.Paused;
        menu.Items.Add(addMarkerItem);

        // 5. Mute mic in recording (FEAT-06) — through the recorder so the session log notes it
        var muteMicItem = ModernMenu.Item("Mute microphone in recording", Glyphs.Microphone, (_, _) =>
        {
            controller.ToggleMicMute();
        }, HotkeyField.DisplayText(hotkeys.MuteMic));
        muteMicItem.Checked = audioEngine.IsMicMuted;
        menu.Items.Add(muteMicItem);

        menu.Items.Add(new ToolStripSeparator());

        // 6. Windows and folders
        menu.Items.Add(ModernMenu.Item("Status", Glyphs.Monitor, (_, _) => showStatusAction(), HotkeyField.DisplayText(hotkeys.ShowStatus)));
        menu.Items.Add(ModernMenu.Item("Recordings library", Glyphs.Library, (_, _) => showLibraryAction()));

        var openFoldersMenu = ModernMenu.Item("Open recordings folder", Glyphs.FolderOpen, null);
        foreach (var loc in settings.Storage.Locations)
        {
            var expanded = Environment.ExpandEnvironmentVariables(loc.Path);
            openFoldersMenu.DropDownItems.Add(ModernMenu.Item(Shorten(expanded), Glyphs.Folder, (_, _) =>
            {
                try
                {
                    if (!Directory.Exists(expanded))
                    {
                        Directory.CreateDirectory(expanded);
                    }

                    Process.Start("explorer.exe", $"\"{expanded}\"");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
                {
                    ModernDialog.Warning(null, "Can't open this folder", $"{expanded}\n\n{ex.Message}");
                }
            }));
        }

        menu.Items.Add(openFoldersMenu);
        menu.Items.Add(ModernMenu.Item("Settings", Glyphs.Settings, (_, _) => showSettingsAction()));

        menu.Items.Add(new ToolStripSeparator());

        // 7. Exit ScreenVault
        menu.Items.Add(ModernMenu.Item("Exit ScreenVault", Glyphs.Close, (_, _) =>
        {
            if (controller.Desired != DesiredState.Stopped &&
                !RecordingPrompts.ConfirmExitWhileRecording(owner))
            {
                return;
            }

            exitAction();
        }));

        return menu;
    }

    private static string Elapsed(HealthSnapshot health) => health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string Shorten(string path)
    {
        const int max = 48;
        if (path.Length <= max)
        {
            return path;
        }

        var root = Path.GetPathRoot(path) ?? string.Empty;
        var keep = Math.Clamp(max - root.Length - 2, 12, path.Length - 1);
        return $"{root}…{path[^keep..]}";
    }
}
