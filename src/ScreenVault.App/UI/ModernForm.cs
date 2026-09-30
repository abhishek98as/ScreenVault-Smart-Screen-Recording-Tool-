using System.Runtime.InteropServices;
using ScreenVault.App.Platform;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI;

public enum WindowChrome
{
    /// <summary>Native title bar, colored to match the palette on Windows 11.</summary>
    Standard,

    /// <summary>No title bar; rounded corners and a drop shadow (flyouts, toasts, popups).</summary>
    Borderless
}

/// <summary>
/// Base class for every ScreenVault window. Provides DPI-correct auto scaling (layouts are
/// authored at 96 DPI), the shared type ramp, live light/dark switching and a Windows 11
/// styled frame.
/// <para>
/// Layout is suspended by this constructor. Derived constructors add their controls and must
/// finish with <c>ResumeLayout(false); PerformLayout();</c> (without calling SuspendLayout
/// themselves) — WinForms then scales everything exactly once for the current DPI.
/// </para>
/// </summary>
public class ModernForm : Form, IThemeAware
{
    private const int WmSettingChange = 0x001A;
    private const int WmThemeChanged = 0x031A;
    private const int WmSysColorChange = 0x0015;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x2;

    private readonly WindowChrome _chrome;

    protected ModernForm(WindowChrome chrome = WindowChrome.Standard)
    {
        _chrome = chrome;

        // Setting AutoScaleDimensions while layout is NOT suspended makes WinForms scale the
        // (still empty) form immediately, and controls added afterwards would never be scaled.
        // Keep layout suspended until the derived constructor calls ResumeLayout(false).
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = Typography.Body;
        DoubleBuffered = true;

        var icon = AppIcon.Get();
        if (icon != null)
        {
            Icon = icon;
        }

        if (chrome == WindowChrome.Borderless)
        {
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(1);
        }

        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Text;
        Theme.Changed += OnThemeChanged;
    }

    protected WindowChrome Chrome => _chrome;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_chrome == WindowChrome.Borderless)
            {
                cp.ClassStyle |= NativeTheme.CsDropShadow;
            }

            return cp;
        }
    }

    /// <summary>Re-applies the active palette to this window and all theme-aware children.</summary>
    public virtual void ApplyTheme()
    {
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Text;
        ApplyFrame();
        foreach (Control child in Controls)
        {
            Theme.ApplyTree(child);
        }

        OnThemeApplied();
        Invalidate(true);
    }

    /// <summary>Hook for derived windows that color things the generic walk cannot reach.</summary>
    protected virtual void OnThemeApplied()
    {
    }

    /// <summary>Lets the user move a borderless window by dragging <paramref name="handle"/>.</summary>
    protected void EnableDrag(Control handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !IsHandleCreated)
            {
                return;
            }

            _ = ReleaseCapture();
            _ = SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFrame();
        if (CaptureExclusion.ExcludeFromCapture)
        {
            CaptureExclusion.Exclude(Handle);
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            // Catch system theme changes that happened while the window was hidden.
            Theme.Refresh();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_chrome == WindowChrome.Borderless && !NativeTheme.IsWindows11)
        {
            // Windows 11 draws a themed 1px border around rounded windows; Windows 10 needs our own.
            using var pen = new Pen(Theme.Current.BorderStrong);
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == WmSettingChange && m.LParam != IntPtr.Zero)
        {
            var area = Marshal.PtrToStringUni(m.LParam);
            if (string.Equals(area, "ImmersiveColorSet", StringComparison.Ordinal))
            {
                Theme.Refresh();
            }
        }
        else if (m.Msg is WmThemeChanged or WmSysColorChange)
        {
            Theme.Refresh();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
        }

        base.Dispose(disposing);
    }

    private void ApplyFrame()
    {
        if (IsHandleCreated)
        {
            NativeTheme.ApplyWindowFrame(Handle, Theme.Current, _chrome == WindowChrome.Borderless ? WindowCorners.Round : WindowCorners.Default);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(ApplyTheme);
        }
        else
        {
            ApplyTheme();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
