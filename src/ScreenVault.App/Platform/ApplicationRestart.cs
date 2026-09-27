using System.Runtime.InteropServices;
using Serilog;

namespace ScreenVault.App.Platform;

public static class ApplicationRestart
{
    private const int RestartNoCrash = 1;
    private const int RestartNoHang = 2;
    private const int RestartNoPatch = 4;
    private const int RestartNoReboot = 8;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string pwzCommandLine, int dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int UnregisterApplicationRestart();

    /// <summary>
    /// Asks Windows to relaunch ScreenVault after a crash, hang or update restart. Only when a
    /// recording was running does the relaunch start recording again — otherwise Windows restarting
    /// the app must never start a recording on its own.
    /// </summary>
    public static void Register(bool resumeRecording)
    {
        Register(resumeRecording ? "--startrecording --minimize-to-tray --recovered" : "--minimize-to-tray --recovered");
    }

    public static void Register(string commandLine)
    {
        try
        {
            var hr = RegisterApplicationRestart(commandLine, 0);
            if (hr == 0)
            {
                Log.Information("Windows Application Restart registered with args: {Args}", commandLine);
            }
            else
            {
                Log.Warning("RegisterApplicationRestart returned HRESULT: 0x{Hr:X}", hr);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not register application restart.");
        }
    }

    public static void Unregister()
    {
        try
        {
            var hr = UnregisterApplicationRestart();
            if (hr != 0)
            {
                Log.Debug("UnregisterApplicationRestart returned HRESULT: 0x{Hr:X}", hr);
            }
        }
        catch
        {
            // Ignore error
        }
    }
}
