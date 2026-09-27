using System.Runtime.InteropServices;
using Serilog;

namespace ScreenVault.App.UI.Theming;

public enum WindowCorners
{
    Default = 0,
    Square = 1,
    Round = 2,
    RoundSmall = 3
}

/// <summary>
/// Thin wrappers over DWM / UxTheme so windows match the app palette: dark title bars
/// (Windows 10 1809+), caption and border colors plus rounded corners (Windows 11), and dark
/// scrollbars for list controls. Every call is best effort and silently ignored on older systems.
/// </summary>
internal static class NativeTheme
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public const int CsDropShadow = 0x00020000;

    public static bool IsWindows11 { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>Colors the non-client frame of a top-level window to match the palette.</summary>
    public static void ApplyWindowFrame(IntPtr hwnd, Palette palette, WindowCorners corners)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        try
        {
            var dark = palette.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
            }

            if (!IsWindows11)
            {
                return;
            }

            var corner = (int)corners;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            if (palette.IsHighContrast)
            {
                return;
            }

            var caption = ToColorRef(palette.Window);
            var text = ToColorRef(palette.Text);
            var border = ToColorRef(palette.IsDark ? palette.BorderStrong : palette.Border);
            _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(ex, "DWM window attributes are not available on this system.");
        }
    }

    /// <summary>Switches the scrollbars of a native list/scrollable control to the dark theme.</summary>
    public static void ApplyScrollbarTheme(Control control, Palette palette)
    {
        if (control == null || !control.IsHandleCreated || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        try
        {
            _ = SetWindowTheme(control.Handle, palette.IsDark && !palette.IsHighContrast ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(ex, "SetWindowTheme is not available on this system.");
        }
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);
}
