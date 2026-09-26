using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

namespace ScreenVault.App.Platform;

public static class StartWithWindows
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppName = "ScreenVault";

    public static bool HasHklmRunEntry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not check HKLM Run registry key.");
            return false;
        }
    }

    public static void CleanDuplicateHkcuEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(AppName) != null)
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                Log.Information("Removed duplicate HKCU Run value because HKLM Run entry exists.");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not clean duplicate HKCU Run entry.");
        }
    }

    public static bool IsEnabled()
    {
        if (HasHklmRunEntry())
        {
            return true;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not check StartWithWindows registry key.");
            return false;
        }
    }

    public static bool IsApprovedByWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, writable: false);
            var val = key?.GetValue(AppName);
            if (val is byte[] bytes && bytes.Length > 0)
            {
                // First byte == 0x02 means enabled in Task Manager; 0x03 means disabled
                return bytes[0] == 0x02;
            }
        }
        catch
        {
            // Default to true if not present
        }
        return true;
    }

    public static void SetEnabled(bool enable, bool startRecording = false)
    {
        if (HasHklmRunEntry())
        {
            // All-users install: Rely on HKLM Run entry.
            // Never write HKCU Run, and remove leftover HKCU value to avoid double launches.
            CleanDuplicateHkcuEntry();
            Log.Information("HKLM Run entry is active; relying on HKLM and skipping HKCU write.");
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var flags = startRecording
                        ? "--autostart --minimize-to-tray --startrecording"
                        : "--autostart --minimize-to-tray";
                    var cmd = $"\"{exePath}\" {flags}";
                    key.SetValue(AppName, cmd);
                    Log.Information("Registered ScreenVault with HKCU Windows Run: {Cmd}", cmd);
                }
            }
            else
            {
                if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, throwOnMissingValue: false);
                    Log.Information("Unregistered ScreenVault from HKCU Windows Run.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update StartWithWindows registry value.");
        }
    }
}
