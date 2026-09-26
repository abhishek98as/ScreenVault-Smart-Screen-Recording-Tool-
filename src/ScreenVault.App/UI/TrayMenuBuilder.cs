using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        Action exitAction)
    {
        var menu = new ContextMenuStrip();
        var health = controller.Health;
        var settings = settingsService.Current;

        // 1. First Item: Start Recording / Stop & Save
        if (health.State is RecorderState.Recording or RecorderState.Paused or RecorderState.Starting)
        {
            var stopItem = new ToolStripMenuItem("■ Stop && Save\tCtrl+Alt+Shift+R", null, async (_, _) =>
            {
                if (settings.General.ConfirmBeforeStop)
                {
                    var res = MessageBox.Show(
                        "Stop and save the recording?",
                        "Stop Recording — ScreenVault",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Question);

                    if (res != DialogResult.OK) return;
                }
                await controller.StopAsync().ConfigureAwait(true);
            });
            stopItem.Font = new Font(stopItem.Font, FontStyle.Bold);
            menu.Items.Add(stopItem);
        }
        else
        {
            var startItem = new ToolStripMenuItem("● Start recording\tCtrl+Alt+Shift+R", null, async (_, _) =>
            {
                await controller.StartAsync().ConfigureAwait(true);
            });
            startItem.Font = new Font(startItem.Font, FontStyle.Bold);
            menu.Items.Add(startItem);
        }

        menu.Items.Add(new ToolStripSeparator());

        // 2. Header (State & Elapsed)
        var stateText = health.State switch
        {
            RecorderState.Recording => health.IsDegraded ? $"● REC {health.Elapsed:hh\\:mm\\:ss} (Degraded)" : $"● REC {health.Elapsed:hh\\:mm\\:ss}",
            RecorderState.Paused => "⏸ Paused",
            RecorderState.Saving => "Saving…",
            RecorderState.Faulted => "Not recording — retrying",
            RecorderState.Starting or RecorderState.Recovering => "Starting…",
            _ => "● Ready"
        };

        var headerItem = new ToolStripMenuItem($"ScreenVault · {stateText}") { Enabled = false };
        headerItem.Font = new Font(headerItem.Font, FontStyle.Bold);
        menu.Items.Add(headerItem);

        // 3. Storage line
        var storageStatus = storageManager?.GetStatus();
        var activePath = storageStatus?.ActiveLocationPath ?? string.Empty;
        var activeLoc = storageStatus?.Locations.FirstOrDefault(l => string.Equals(l.Path, activePath, StringComparison.OrdinalIgnoreCase));
        var freeGb = (activeLoc?.AvailableFreeBytes ?? 0) / (1024.0 * 1024.0 * 1024.0);
        var locDisplay = string.IsNullOrEmpty(activePath) ? "Default folder" : Environment.ExpandEnvironmentVariables(activePath);
        var saveInfo = new ToolStripMenuItem($"Saving to: {locDisplay} · {freeGb:F1} GB free") { Enabled = false };
        menu.Items.Add(saveInfo);

        menu.Items.Add(new ToolStripSeparator());

        // 4. Pause / Resume
        if (health.State == RecorderState.Paused)
        {
            var resumeItem = new ToolStripMenuItem("▶ Resume\tCtrl+Alt+Shift+P", null, async (_, _) =>
            {
                await controller.ResumeAsync().ConfigureAwait(true);
            });
            menu.Items.Add(resumeItem);
        }
        else
        {
            var pauseItem = new ToolStripMenuItem("⏸ Pause\tCtrl+Alt+Shift+P", null, async (_, _) =>
            {
                await controller.PauseAsync().ConfigureAwait(true);
            })
            {
                Enabled = health.State == RecorderState.Recording
            };
            menu.Items.Add(pauseItem);
        }

        menu.Items.Add(new ToolStripSeparator());

        // 5. Add Marker
        var addMarkerItem = new ToolStripMenuItem("Add marker…\tCtrl+Alt+Shift+M", null, (_, _) =>
        {
            var timeStr = health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            using var dlg = new MarkerNoteForm(timeStr);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                markerService.AddMarker(dlg.NoteText, "User");
            }
        })
        {
            Enabled = health.State is RecorderState.Recording or RecorderState.Paused
        };
        menu.Items.Add(addMarkerItem);

        // 6. Mute mic in recording (FEAT-06)
        var muteMicItem = new ToolStripMenuItem("🎙 Mute mic in recording\tCtrl+Alt+Shift+X", null, (_, _) =>
        {
            audioEngine.SetMicMute(!audioEngine.IsMicMuted);
        })
        {
            Checked = audioEngine.IsMicMuted
        };
        menu.Items.Add(muteMicItem);

        menu.Items.Add(new ToolStripSeparator());

        // 7. Status, Library, Open recordings folder, Settings
        menu.Items.Add(new ToolStripMenuItem("Status…\tCtrl+Alt+Shift+S", null, (_, _) => showStatusAction()));
        menu.Items.Add(new ToolStripMenuItem("Recordings library…", null, (_, _) => showLibraryAction()));

        var openFoldersMenu = new ToolStripMenuItem("Open recordings folder");
        foreach (var loc in settings.Storage.Locations)
        {
            var expanded = Environment.ExpandEnvironmentVariables(loc.Path);
            var item = new ToolStripMenuItem(expanded, null, (_, _) =>
            {
                if (!Directory.Exists(expanded))
                {
                    Directory.CreateDirectory(expanded);
                }
                Process.Start("explorer.exe", $"\"{expanded}\"");
            });
            openFoldersMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(openFoldersMenu);

        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => showSettingsAction()));

        menu.Items.Add(new ToolStripSeparator());

        // 8. Exit ScreenVault
        var exitItem = new ToolStripMenuItem("Exit ScreenVault", null, (_, _) =>
        {
            if (controller.State is RecorderState.Recording or RecorderState.Paused)
            {
                var res = MessageBox.Show(
                    "Recording is active. Stop recording and exit?",
                    "Exit ScreenVault",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);

                if (res != DialogResult.OK) return;
            }
            exitAction();
        });
        menu.Items.Add(exitItem);

        return menu;
    }
}
