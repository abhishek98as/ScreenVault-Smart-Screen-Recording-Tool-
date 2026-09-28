using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.Platform;

public enum ResolvedPlayerType
{
    /// <summary>VLC, chosen explicitly in the settings file.</summary>
    Vlc,

    /// <summary>A program chosen explicitly in the settings file.</summary>
    Custom,

    /// <summary>The app Windows opens this kind of file with.</summary>
    SystemDefault,

    /// <summary>No suitable app is set: let the user pick one in Windows' "Open with" chooser.</summary>
    AskUser
}

/// <param name="ExecutablePath">The player program, when known (used to start at a marker).</param>
public sealed record ResolvedPlayer(ResolvedPlayerType Type, string? ExecutablePath);

/// <summary>
/// Opens recordings in the user's own video app, exactly like double-clicking them in Explorer.
/// ScreenVault has no player of its own; when Windows has no app for the file type, Windows'
/// "How do you want to open this file?" chooser lets the user pick one (and keep it).
/// </summary>
public sealed class PlayerLauncher
{
    private const int ErrorNoAssociation = 1155;
    private const int ErrorCancelled = 1223;

    private readonly ISettingsService _settingsService;

    public PlayerLauncher(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public ResolvedPlayer ResolvePlayer(string targetFilePath)
    {
        var playback = _settingsService.Current.Playback;

        if (playback.Player == PlaybackPlayer.Custom && !string.IsNullOrEmpty(playback.CustomPlayerPath) && File.Exists(playback.CustomPlayerPath))
        {
            return new ResolvedPlayer(ResolvedPlayerType.Custom, playback.CustomPlayerPath);
        }

        if (playback.Player == PlaybackPlayer.Vlc && SafeFindVlcPath() is { } vlc)
        {
            return new ResolvedPlayer(ResolvedPlayerType.Vlc, vlc);
        }

        // Auto, SystemDefault and the retired built-in player all use the Windows default app.
        var extension = Path.GetExtension(targetFilePath);
        var app = FileAssociation.Find(extension);
        if (app == null)
        {
            // Nothing set for .mkv/.mp4 (Windows will ask anyway) or nothing we trust for .ts.
            return string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase)
                ? new ResolvedPlayer(ResolvedPlayerType.AskUser, null)
                : new ResolvedPlayer(ResolvedPlayerType.SystemDefault, null);
        }

        // ".ts" is also TypeScript: never hand a recording to a code editor.
        if (app.IsCodeEditor)
        {
            return new ResolvedPlayer(ResolvedPlayerType.AskUser, null);
        }

        return new ResolvedPlayer(ResolvedPlayerType.SystemDefault, app.ExecutablePath);
    }

    /// <summary>Plays <paramref name="filePath"/>, from <paramref name="startSec"/> when the player supports it.</summary>
    public bool Launch(string filePath, double? startSec = null, IWin32Window? owner = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Log.Warning("PlayerLauncher: file does not exist: {Path}", filePath);
            UI.ModernDialog.Warning(owner, "Can't play this file", $"The file no longer exists:\n{filePath}");
            return false;
        }

        var resolved = ResolvePlayer(filePath);
        try
        {
            switch (resolved.Type)
            {
                case ResolvedPlayerType.AskUser:
                    return ShowOpenWith(filePath, owner);

                case ResolvedPlayerType.SystemDefault:
                    if (startSec is > 0 && TryStartAt(resolved.ExecutablePath, filePath, startSec.Value))
                    {
                        return true;
                    }

                    return OpenWithDefaultApp(filePath, owner);

                case ResolvedPlayerType.Vlc:
                    StartProgram(resolved.ExecutablePath!, VlcArguments(filePath, startSec));
                    return true;

                default:
                    StartProgram(resolved.ExecutablePath!, [filePath]);
                    return true;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or COMException)
        {
            Log.Error(ex, "Failed to open {Path} in a video player", filePath);
            UI.ModernDialog.Error(owner, "Couldn't open the video", ex.Message);
            return false;
        }
    }

    /// <summary>Windows' "How do you want to open this file?" chooser; the pick can be kept for next time.</summary>
    public static bool OpenWithDialog(string filePath, IWin32Window? owner = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            return ShowOpenWith(filePath, owner);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or COMException)
        {
            Log.Warning(ex, "Failed to show the Open with chooser for {Path}", filePath);
            return false;
        }
    }

    private static bool OpenWithDefaultApp(string filePath, IWin32Window? owner)
    {
        Log.Information("Opening {Path} with the default app", filePath);
        try
        {
            // ErrorDialog lets Windows show its own UI (including "Open with" for unknown types).
            Process.Start(new ProcessStartInfo(filePath)
            {
                UseShellExecute = true,
                ErrorDialog = true,
                ErrorDialogParentHandle = owner?.Handle ?? IntPtr.Zero
            })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoAssociation)
        {
            return ShowOpenWith(filePath, owner);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    private static bool ShowOpenWith(string filePath, IWin32Window? owner)
    {
        Log.Information("Asking which app should open {Path}", filePath);
        try
        {
            var info = new OpenAsInfo
            {
                File = filePath,
                Flags = OpenAsInfoFlags.AllowRegistration | OpenAsInfoFlags.RegisterExtension | OpenAsInfoFlags.Execute
            };
            var hr = SHOpenWithDialog(owner?.Handle ?? IntPtr.Zero, ref info);
            if (hr == unchecked((int)0x800704C7))
            {
                return false; // The user closed the chooser.
            }

            Marshal.ThrowExceptionForHR(hr);
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or COMException)
        {
            // Very old shells: the classic dialog.
            Log.Debug(ex, "SHOpenWithDialog unavailable; using OpenAs_RunDLL.");
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {filePath}") { UseShellExecute = true })?.Dispose();
            return true;
        }
    }

    /// <summary>Starts well-known players that accept a start position directly at <paramref name="startSec"/>.</summary>
    private static bool TryStartAt(string? player, string filePath, double startSec)
    {
        if (string.IsNullOrEmpty(player) || !File.Exists(player))
        {
            return false;
        }

        var seconds = ((int)Math.Max(0, startSec)).ToString(CultureInfo.InvariantCulture);
        string[]? args = Path.GetFileNameWithoutExtension(player).ToLowerInvariant() switch
        {
            "vlc" => VlcArguments(filePath, startSec),
            "mpv" => [$"--start={seconds}", filePath],
            "mpc-hc64" or "mpc-hc" or "mpc-be64" or "mpc-be" => [filePath, "/start", ((long)(startSec * 1000)).ToString(CultureInfo.InvariantCulture)],
            "potplayermini64" or "potplayermini" or "potplayer64" or "potplayer" => [filePath, $"/seek={seconds}"],
            _ => null
        };

        if (args == null)
        {
            return false;
        }

        StartProgram(player, args);
        return true;
    }

    private static string[] VlcArguments(string filePath, double? startSec) => startSec is > 0
        ? [$"--start-time={(int)Math.Max(0, startSec.Value)}", filePath]
        : [filePath];

    private static void StartProgram(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Log.Information("Launching player: {Exe} {Args}", psi.FileName, string.Join(" ", psi.ArgumentList));
        Process.Start(psi)?.Dispose();
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

    /// <summary>What Windows opens a file type with (respects the user's "Always use this app" choice).</summary>
    private sealed record FileAssociation(string? ExecutablePath, string? AppName)
    {
        private static readonly string[] EditorExecutables =
        [
            "code", "code - insiders", "cursor", "windsurf", "zed", "devenv", "notepad", "notepad++",
            "sublime_text", "atom", "webstorm64", "webstorm", "rider64", "idea64", "phpstorm64",
            "pycharm64", "fleet", "gvim", "nvim-qt", "emeditor", "textpad", "wordpad"
        ];

        private static readonly string[] EditorNames =
        [
            "Visual Studio", "Notepad", "Sublime", "WebStorm", "JetBrains", "Cursor", "Windsurf", "TypeScript"
        ];

        public bool IsCodeEditor =>
            (ExecutablePath != null && EditorExecutables.Contains(Path.GetFileNameWithoutExtension(ExecutablePath), StringComparer.OrdinalIgnoreCase)) ||
            (AppName != null && EditorNames.Any(name => AppName.Contains(name, StringComparison.OrdinalIgnoreCase)));

        /// <summary>The associated app, or null when Windows has none (or can't be asked).</summary>
        public static FileAssociation? Find(string extension)
        {
            if (string.IsNullOrEmpty(extension))
            {
                return null;
            }

            var exe = Query(extension, AssocStr.Executable);
            var name = Query(extension, AssocStr.FriendlyAppName);
            return exe == null && name == null ? null : new FileAssociation(exe, name);
        }

        private static string? Query(string extension, AssocStr what)
        {
            try
            {
                var buffer = new char[1024];
                var length = (uint)buffer.Length;
                var hr = AssocQueryString(AssocF.InitIgnoreUnknown | AssocF.NoTruncate, what, extension, "open", buffer, ref length);
                if (hr != 0 || length <= 1)
                {
                    return null;
                }

                var value = new string(buffer, 0, (int)length - 1).Trim();
                return value.Length == 0 || value.EndsWith("OpenWith.exe", StringComparison.OrdinalIgnoreCase) ? null : value;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }
    }

    [Flags]
    private enum AssocF : uint
    {
        NoTruncate = 0x20,
        InitIgnoreUnknown = 0x400
    }

    private enum AssocStr
    {
        Executable = 2,
        FriendlyAppName = 4
    }

    [Flags]
    private enum OpenAsInfoFlags : uint
    {
        AllowRegistration = 0x1,
        RegisterExtension = 0x2,
        Execute = 0x4
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string File;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Class;

        public OpenAsInfoFlags Flags;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(AssocF flags, AssocStr str, string pszAssoc, string? pszExtra, [Out] char[] pszOut, ref uint pcchOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo oOAI);
}
