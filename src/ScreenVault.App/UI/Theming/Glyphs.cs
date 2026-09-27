using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ScreenVault.App.UI.Theming;

/// <summary>
/// Icons from the Windows icon fonts: "Segoe Fluent Icons" (Windows 11) with a fallback to
/// "Segoe MDL2 Assets" (Windows 10). Both share the code points below, so icons stay crisp at
/// any DPI without shipping image assets. If neither font exists, icons are simply omitted.
/// </summary>
public static class Glyphs
{
    public const char None = '\0';

    public const char Add = '\uE710';
    public const char Cancel = '\uE711';
    public const char Settings = '\uE713';
    public const char Video = '\uE714';
    public const char Pin = '\uE718';
    public const char Microphone = '\uE720';
    public const char Search = '\uE721';
    public const char Refresh = '\uE72C';
    public const char Lock = '\uE72E';
    public const char CheckMark = '\uE73E';
    public const char Delete = '\uE74D';
    public const char Save = '\uE74E';
    public const char Keyboard = '\uE765';
    public const char Volume = '\uE767';
    public const char Play = '\uE768';
    public const char Pause = '\uE769';
    public const char ChevronDown = '\uE70D';
    public const char ChevronUp = '\uE70E';
    public const char ChevronRight = '\uE76C';
    public const char Unlock = '\uE785';
    public const char Warning = '\uE7BA';
    public const char Flag = '\uE7C1';
    public const char Record = '\uE7C8';
    public const char Monitor = '\uE7F4';
    public const char Headphones = '\uE7F6';
    public const char Clock = '\uE823';
    public const char FolderOpen = '\uE838';
    public const char Pinned = '\uE840';
    public const char Rename = '\uE8AC';
    public const char Folder = '\uE8B7';
    public const char Close = '\uE8BB';
    public const char Cut = '\uE8C6';
    public const char Copy = '\uE8C8';
    public const char Library = '\uE8F1';
    public const char OpenWith = '\uE7AC';
    public const char Completed = '\uE930';
    public const char Info = '\uE946';
    public const char Diagnostic = '\uE9D9';
    public const char Shield = '\uEA18';
    public const char Error = '\uEA39';
    public const char HardDrive = '\uEDA2';
    public const char Stop = '\uE71A';
    public const char Merge = '\uE71B';
    public const char Export = '\uEDE1';
    public const char Tag = '\uE8EC';
    public const char Speed = '\uEC4A';
    public const char Move = '\uE8DE';

    private static readonly Lazy<string?> Family = new(ResolveFamily);
    private static readonly ConcurrentDictionary<float, Font> Fonts = new();

    public static bool Available => Family.Value != null;

    /// <summary>Returns the icon font at the given point size, or null if no icon font is installed.</summary>
    public static Font? GetFont(float sizeInPoints)
    {
        var family = Family.Value;
        if (family == null)
        {
            return null;
        }

        var rounded = MathF.Round(sizeInPoints * 2f) / 2f;
        return Fonts.GetOrAdd(rounded, size => new Font(family, size, FontStyle.Regular, GraphicsUnit.Point));
    }

    /// <summary>Draws a glyph centered in <paramref name="bounds"/>.</summary>
    public static void Draw(Graphics g, char glyph, Rectangle bounds, Color color, float sizeInPoints)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (glyph == None)
        {
            return;
        }

        var font = GetFont(sizeInPoints);
        if (font == null)
        {
            return;
        }

        TextRenderer.DrawText(
            g,
            glyph.ToString(),
            font,
            bounds,
            color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    /// <summary>Renders a glyph to a transparent bitmap (for menus and image lists).</summary>
    public static Bitmap? ToBitmap(char glyph, Color color, int pixelSize)
    {
        var family = Family.Value;
        if (family == null || glyph == None || pixelSize <= 0)
        {
            return null;
        }

        var bmp = new Bitmap(pixelSize, pixelSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(family, pixelSize * 0.78f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, pixelSize, pixelSize), format);
        return bmp;
    }

    private static string? ResolveFamily()
    {
        if (Typography.IsInstalled("Segoe Fluent Icons"))
        {
            return "Segoe Fluent Icons";
        }

        return Typography.IsInstalled("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : null;
    }
}
