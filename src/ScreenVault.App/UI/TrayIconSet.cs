using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ScreenVault.Core.Recording;

namespace ScreenVault.App.UI;

public static class TrayIconSet
{
    private static readonly ConcurrentDictionary<string, Icon> IconCache = new();

    public static Icon CreateIcon(RecorderState state, bool isDegraded, int size = 32)
    {
        var cacheKey = $"{state}_{isDegraded}_{size}";
        return IconCache.GetOrAdd(cacheKey, _ => GenerateIcon(state, isDegraded, size));
    }

    public static Icon GetIcon(RecorderState state, bool isDegraded, int size = 32) =>
        CreateIcon(state, isDegraded, size);

    private static Icon GenerateIcon(RecorderState state, bool isDegraded, int size)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float pad = size * 0.08f;
            float d = size - (2 * pad);

            switch (state)
            {
                case RecorderState.Recording:
                    var recColor = Color.FromArgb(217, 48, 37); // Red #D93025
                    DrawCircleWithOutline(g, pad, pad, d, recColor);
                    if (isDegraded)
                    {
                        DrawWarningBadge(g, size);
                    }
                    break;

                case RecorderState.Paused:
                    var pausedColor = Color.FromArgb(107, 107, 107); // Gray #6B6B6B
                    DrawCircleWithOutline(g, pad, pad, d, pausedColor);
                    DrawPauseBars(g, size);
                    break;

                case RecorderState.Saving:
                    var savingColor = Color.FromArgb(107, 107, 107); // Gray #6B6B6B
                    DrawCircleWithOutline(g, pad, pad, d, savingColor);
                    DrawArrowBadge(g, size, Color.White);
                    break;

                case RecorderState.Starting:
                case RecorderState.Recovering:
                    var startingColor = Color.FromArgb(30, 142, 62); // Green #1E8E3E
                    DrawCircleWithOutline(g, pad, pad, d, startingColor);
                    DrawArrowBadge(g, size, Color.White);
                    break;

                case RecorderState.Faulted:
                    var faultedColor = Color.FromArgb(217, 48, 37);
                    DrawCircleWithOutline(g, pad, pad, d, faultedColor);
                    DrawExclamationMark(g, size);
                    break;

                case RecorderState.Idle:
                case RecorderState.Stopping:
                default:
                    var readyColor = Color.FromArgb(30, 142, 62); // Green #1E8E3E
                    DrawCircleWithOutline(g, pad, pad, d, readyColor);
                    break;
            }
        }

        var hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawCircleWithOutline(Graphics g, float x, float y, float d, Color fill)
    {
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(ControlPaint.Dark(fill), 1.5f);
        g.FillEllipse(brush, x, y, d, d);
        g.DrawEllipse(pen, x, y, d, d);
    }

    private static void DrawPauseBars(Graphics g, int size)
    {
        float w = size * 0.14f;
        float h = size * 0.44f;
        float y = (size - h) / 2f;
        g.FillRectangle(Brushes.White, size * 0.32f, y, w, h);
        g.FillRectangle(Brushes.White, size * 0.54f, y, w, h);
    }

    private static void DrawArrowBadge(Graphics g, int size, Color color)
    {
        using var pen = new Pen(color, Math.Max(1.5f, size * 0.10f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.ArrowAnchor
        };
        var rect = new RectangleF(size * 0.28f, size * 0.28f, size * 0.44f, size * 0.44f);
        g.DrawArc(pen, rect, 45, 270);
    }

    private static void DrawWarningBadge(Graphics g, int size)
    {
        var badgeSize = size * 0.45f;
        var p1 = new PointF(size - 1, size - badgeSize);
        var p2 = new PointF(size - 1, size - 1);
        var p3 = new PointF(size - badgeSize, size - 1);

        using var badgeBrush = new SolidBrush(Color.FromArgb(255, 140, 0));
        g.FillPolygon(badgeBrush, [p1, p2, p3]);
    }

    private static void DrawExclamationMark(Graphics g, int size)
    {
        using var brush = new SolidBrush(Color.White);
        var barWidth = Math.Max(2f, size * 0.14f);
        var barHeight = size * 0.38f;
        var left = (size - barWidth) / 2f;
        var top = size * 0.20f;
        g.FillRectangle(brush, left, top, barWidth, barHeight);

        var dotSize = barWidth;
        var dotTop = top + barHeight + (size * 0.08f);
        g.FillEllipse(brush, left, dotTop, dotSize, dotSize);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
