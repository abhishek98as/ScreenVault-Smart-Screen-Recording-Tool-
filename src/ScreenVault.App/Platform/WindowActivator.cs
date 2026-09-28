using System.Runtime.InteropServices;

namespace ScreenVault.App.Platform;

/// <summary>Brings ScreenVault windows in front of whatever app the user is in.</summary>
internal static class WindowActivator
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    /// <summary>
    /// Shows <paramref name="form"/> on top of every other window and tries to give it the focus.
    /// Windows may refuse the focus (e.g. when a hotkey or a timer opened the window), so the
    /// window is first lifted to the top of the stack, which Windows always allows: it is then
    /// visible and one click away even when the focus stays with the other app.
    /// </summary>
    public static void BringToFront(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsDisposed)
        {
            return;
        }

        if (!form.Visible)
        {
            form.Show();
        }

        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        const uint flags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow;
        _ = SetWindowPos(form.Handle, HwndTopmost, 0, 0, 0, 0, flags);
        if (!form.TopMost)
        {
            // Back to a normal window, now above all other normal windows.
            _ = SetWindowPos(form.Handle, HwndNoTopmost, 0, 0, 0, 0, flags);
        }

        form.Activate();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
