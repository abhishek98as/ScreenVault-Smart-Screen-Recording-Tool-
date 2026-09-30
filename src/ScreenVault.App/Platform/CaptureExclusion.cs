using System.Runtime.InteropServices;
using Serilog;

namespace ScreenVault.App.Platform;

/// <summary>
/// Excludes a window from desktop capture (screen recording, screenshots, screen sharing).
/// Uses SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) available on Windows 10 2004+.
/// On older Windows versions the call is a no-op.
/// </summary>
public static class CaptureExclusion
{
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    /// <summary>Exclude <paramref name="hwnd"/> from screen captures.</summary>
    public static void Exclude(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
        {
            // Silently ignore on older Windows — the API simply doesn't exist or isn't supported.
            Log.Debug("SetWindowDisplayAffinity(EXCLUDE) failed for hwnd {Hwnd}: {Error}", hwnd, Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Allow <paramref name="hwnd"/> to appear in screen captures again.</summary>
    public static void Allow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!SetWindowDisplayAffinity(hwnd, WDA_NONE))
        {
            Log.Debug("SetWindowDisplayAffinity(NONE) failed for hwnd {Hwnd}: {Error}", hwnd, Marshal.GetLastWin32Error());
        }
    }

    public static bool ExcludeFromCapture { get; set; } = true;

    /// <summary>
    /// Apply the current setting to <paramref name="hwnd"/>: exclude if <paramref name="hide"/> is true, allow otherwise.
    /// </summary>
    public static void Apply(IntPtr hwnd, bool hide)
    {
        if (hide) Exclude(hwnd); else Allow(hwnd);
    }

    /// <summary>Updates the capture exclusion setting and applies it to all currently open forms.</summary>
    public static void ApplyToOpenForms(bool exclude)
    {
        ExcludeFromCapture = exclude;
        try
        {
            foreach (Form form in Application.OpenForms)
            {
                if (form.IsHandleCreated)
                {
                    Apply(form.Handle, exclude);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to apply capture exclusion to open forms.");
        }
    }
}
