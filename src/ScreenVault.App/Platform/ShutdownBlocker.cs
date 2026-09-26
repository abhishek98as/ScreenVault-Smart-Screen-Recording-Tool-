using System.Runtime.InteropServices;
using Serilog;

namespace ScreenVault.App.Platform;

public static class ShutdownBlocker
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszBuff);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    public static void BlockShutdown(IntPtr windowHandle, string reason)
    {
        if (windowHandle == IntPtr.Zero) return;
        try
        {
            ShutdownBlockReasonCreate(windowHandle, reason);
            Log.Information("Shutdown block registered: {Reason}", reason);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not set shutdown block reason.");
        }
    }

    public static void UnblockShutdown(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;
        try
        {
            ShutdownBlockReasonDestroy(windowHandle);
        }
        catch
        {
            // Ignore error
        }
    }
}
