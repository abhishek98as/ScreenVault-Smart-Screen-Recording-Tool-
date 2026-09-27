using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.Platform;

public enum ResolvedPlayerType
{
    Vlc,
    Ffplay,
    Custom,
    SystemDefault,
    None
}

public sealed record ResolvedPlayer(ResolvedPlayerType Type, string? ExecutablePath);

public sealed class PlayerLauncher
{
    private readonly ISettingsService _settingsService;

    public PlayerLauncher(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public ResolvedPlayer ResolvePlayer(string targetFilePath)
    {
        var settings = _settingsService.Current;
        var ext = Path.GetExtension(targetFilePath).ToLowerInvariant();
        var isTs = ext == ".ts";

        var chosen = settings.Playback.Player;

        if (chosen == PlaybackPlayer.Custom && !string.IsNullOrEmpty(settings.Playback.CustomPlayerPath) && File.Exists(settings.Playback.CustomPlayerPath))
        {
            return new ResolvedPlayer(ResolvedPlayerType.Custom, settings.Playback.CustomPlayerPath);
        }

        if (chosen == PlaybackPlayer.Vlc)
        {
            var vlc = SafeFindVlcPath();
            if (vlc != null) return new ResolvedPlayer(ResolvedPlayerType.Vlc, vlc);
        }

        if (chosen == PlaybackPlayer.Ffplay)
        {
            var ffplay = SafeFindFfplayPath(settings);
            if (ffplay != null) return new ResolvedPlayer(ResolvedPlayerType.Ffplay, ffplay);
        }

        if (chosen == PlaybackPlayer.SystemDefault && !isTs)
        {
            return new ResolvedPlayer(ResolvedPlayerType.SystemDefault, null);
        }

        // Auto resolution order:
        // 1. VLC
        var autoVlc = SafeFindVlcPath();
        if (autoVlc != null)
        {
            return new ResolvedPlayer(ResolvedPlayerType.Vlc, autoVlc);
        }

        // 2. Bundled ffplay.exe
        var autoFfplay = SafeFindFfplayPath(settings);
        if (autoFfplay != null)
        {
            return new ResolvedPlayer(ResolvedPlayerType.Ffplay, autoFfplay);
        }

        // 3. System default (only for .mkv/.mp4, never for .ts)
        if (!isTs)
        {
            return new ResolvedPlayer(ResolvedPlayerType.SystemDefault, null);
        }

        return new ResolvedPlayer(ResolvedPlayerType.None, null);
    }

    public bool Launch(string filePath, double? startSec = null, string? windowTitle = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Log.Warning("PlayerLauncher: file does not exist: {Path}", filePath);
            UI.ModernDialog.Warning(null, "Can't play this file", $"The file no longer exists:\n{filePath}");
            return false;
        }

        var resolved = ResolvePlayer(filePath);
        if (resolved.Type == ResolvedPlayerType.None)
        {
            var msg = "No video player found. Install VLC (free) or choose a player in Settings → Advanced.\n\n" +
                      "(Note: .ts recording parts are not opened with system default to avoid opening code editors).";
            Log.Warning("PlayerLauncher: {Msg}", msg);
            UI.ModernDialog.Warning(null, "No video player available", msg);
            return false;
        }

        try
        {
            if (resolved.Type == ResolvedPlayerType.SystemDefault)
            {
                Log.Information("Launching system default player for {Path}", filePath);
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                return true;
            }

            var psi = new ProcessStartInfo(resolved.ExecutablePath!)
            {
                UseShellExecute = false
            };

            if (resolved.Type == ResolvedPlayerType.Vlc)
            {
                if (startSec.HasValue && startSec.Value > 0)
                {
                    var sec = (int)Math.Max(0, startSec.Value);
                    psi.ArgumentList.Add($"--start-time={sec}");
                }
                psi.ArgumentList.Add(filePath);
            }
            else if (resolved.Type == ResolvedPlayerType.Ffplay)
            {
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("warning");
                if (!string.IsNullOrEmpty(windowTitle))
                {
                    psi.ArgumentList.Add("-window_title");
                    psi.ArgumentList.Add(windowTitle);
                }
                else
                {
                    psi.ArgumentList.Add("-window_title");
                    psi.ArgumentList.Add($"ScreenVault – {Path.GetFileName(filePath)}");
                }

                if (startSec.HasValue && startSec.Value > 0)
                {
                    var sec = (int)Math.Max(0, startSec.Value);
                    psi.ArgumentList.Add("-ss");
                    psi.ArgumentList.Add(sec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                psi.ArgumentList.Add(filePath);
            }
            else // Custom
            {
                psi.ArgumentList.Add(filePath);
            }

            Log.Information("Launching player: {Exe} {Args}", psi.FileName, string.Join(" ", psi.ArgumentList));
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch video player for {Path}", filePath);
            UI.ModernDialog.Error(null, "Couldn't start the video player", ex.Message);
            return false;
        }
    }

    private static string? SafeFindVlcPath()
    {
        try
        {
            return FindVlcPath();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not look up VLC.");
            return null;
        }
    }

    private static string? SafeFindFfplayPath(AppSettings settings)
    {
        try
        {
            return FindFfplayPath(settings);
        }
        catch (Exception ex)
        {
            // FfmpegLocator throws when FFmpeg is missing; that only means ffplay isn't available.
            Log.Debug(ex, "Could not look up ffplay.");
            return null;
        }
    }

    public static void OpenWithDialog(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {filePath}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to invoke OpenAs dialog for {Path}", filePath);
        }
    }

    public static string? FindVlcPath()
    {
        // 1. Registry App Paths
        string[] appPathKeys = [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\vlc.exe",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\vlc.exe"
        ];

        foreach (var subKey in appPathKeys)
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(subKey);
            var val = hklm?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(val) && File.Exists(val)) return val;

            using var hkcu = Registry.CurrentUser.OpenSubKey(subKey);
            val = hkcu?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(val) && File.Exists(val)) return val;
        }

        // 2. VideoLAN registry install dir
        string[] vlcKeys = [
            @"SOFTWARE\VideoLAN\VLC",
            @"SOFTWARE\WOW6432Node\VideoLAN\VLC"
        ];
        foreach (var subKey in vlcKeys)
        {
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            var dir = (k?.GetValue("InstallDir") ?? k?.GetValue(null)) as string;
            if (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "vlc.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 3. Program Files common paths
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] standard = [
            Path.Combine(pf, "VideoLAN", "VLC", "vlc.exe"),
            Path.Combine(pf86, "VideoLAN", "VLC", "vlc.exe")
        ];

        foreach (var p in standard)
        {
            if (File.Exists(p)) return p;
        }

        return null;
    }

    public static string? FindFfplayPath(AppSettings settings)
    {
        var locator = new FfmpegLocator();
        var paths = locator.Locate(settings.Advanced.FfmpegPath);
        var dir = Path.GetDirectoryName(paths.FfmpegPath);
        if (!string.IsNullOrEmpty(dir))
        {
            var ffplay = Path.Combine(dir, "ffplay.exe");
            if (File.Exists(ffplay)) return ffplay;
        }

        var appBase = AppDomain.CurrentDomain.BaseDirectory;
        var bundled = Path.Combine(appBase, "ffmpeg", "ffplay.exe");
        if (File.Exists(bundled)) return bundled;

        var toolsBundled = Path.Combine(appBase, "..", "..", "..", "..", "tools", "ffmpeg", "ffplay.exe");
        if (File.Exists(toolsBundled)) return Path.GetFullPath(toolsBundled);

        return null;
    }
}
