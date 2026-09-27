using System.Drawing.Drawing2D;

namespace ScreenVault.App.UI.Theming;

/// <summary>Small GDI+ helpers shared by the custom-painted controls.</summary>
internal static class Draw
{
    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 1f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, RectangleF bounds, float radius)
    {
        if (color.A == 0)
        {
            return;
        }

        using var brush = new SolidBrush(color);
        using var path = RoundedRect(bounds, radius);
        g.FillPath(brush, path);
    }

    /// <summary>Strokes a rounded rectangle fully inside <paramref name="bounds"/> (crisp 1px lines).</summary>
    public static void StrokeRounded(Graphics g, Color color, RectangleF bounds, float radius, float width = 1f)
    {
        if (color.A == 0)
        {
            return;
        }

        var half = width / 2f;
        var inner = new RectangleF(bounds.X + half, bounds.Y + half, bounds.Width - width, bounds.Height - width);
        using var pen = new Pen(color, width);
        using var path = RoundedRect(inner, Math.Max(0f, radius - half));
        g.DrawPath(pen, path);
    }

    public static void FillCircle(Graphics g, Color color, RectangleF bounds)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, bounds);
    }

    /// <summary>Linear interpolation between two colors (t = 0 → a, t = 1 → b).</summary>
    public static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.A + ((b.A - a.A) * t)),
            (int)Math.Round(a.R + ((b.R - a.R) * t)),
            (int)Math.Round(a.G + ((b.G - a.G) * t)),
            (int)Math.Round(a.B + ((b.B - a.B) * t)));
    }

    public static Color WithAlpha(Color color, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), color);

    /// <summary>Background color of the nearest ancestor (what a control's rounded corners should reveal).</summary>
    public static Color ParentBackground(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        for (var parent = control.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.BackColor.A == 255)
            {
                return parent.BackColor;
            }
        }

        return Theme.Current.Window;
    }

    /// <summary>Scale factor of a control relative to 96 DPI.</summary>
    public static float Scale(Control control) => control.DeviceDpi / 96f;

    public static void PrepareHighQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
    }

    /// <summary>Draws the keyboard focus ring used by all custom controls.</summary>
    public static void FocusRing(Graphics g, Control control, RectangleF bounds, float radius)
    {
        var scale = Scale(control);
        var width = Math.Max(1.5f, 2f * scale);
        StrokeRounded(g, Theme.Current.IsHighContrast ? SystemColors.Highlight : Theme.Current.Accent, bounds, radius, width);
    }
}
