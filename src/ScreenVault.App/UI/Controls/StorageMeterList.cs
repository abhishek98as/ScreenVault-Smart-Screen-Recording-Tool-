using System.ComponentModel;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>One storage location as shown in the status window.</summary>
public sealed record StorageMeterRow(string Path, long FreeBytes, long TotalBytes, string StateText, Tone StateTone, bool IsActive);

/// <summary>
/// Painted list of storage locations: path, free space, a usage bar and a colored state line.
/// Rows are drawn rather than hosted as child controls, so refreshing every second is free.
/// </summary>
public sealed class StorageMeterList : Control, IThemeAware
{
    private const int RowHeightLogical = 50;
    private IReadOnlyList<StorageMeterRow> _rows = [];

    public StorageMeterList()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Font = Typography.Body;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Storage locations";
    }

    [Browsable(false)]
    public int PreferredHeight => Math.Max(1, _rows.Count) * LogicalToDeviceUnits(RowHeightLogical);

    public void ApplyTheme() => Invalidate();

    public void SetRows(IReadOnlyList<StorageMeterRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.SequenceEqual(_rows))
        {
            return;
        }

        _rows = rows;
        AccessibleDescription = string.Join("; ", rows.Select(r => $"{r.Path}: {FormatBytes(r.FreeBytes)} free, {r.StateText}"));
        Height = PreferredHeight;
        Invalidate();
    }

    public static string FormatBytes(long bytes)
    {
        const double Gb = 1024d * 1024d * 1024d;
        var gb = bytes / Gb;
        return gb >= 100
            ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{gb:F0} GB")
            : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{gb:F1} GB");
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.Clear(Draw.ParentBackground(this));
        Draw.PrepareHighQuality(g);

        if (_rows.Count == 0)
        {
            TextRenderer.DrawText(g, "No storage locations configured", Typography.Caption, ClientRectangle, p.TextTertiary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
            return;
        }

        var scale = Draw.Scale(this);
        var rowHeight = LogicalToDeviceUnits(RowHeightLogical);
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var top = i * rowHeight;

            var glyphSize = (int)(18 * scale);
            Glyphs.Draw(g, Glyphs.HardDrive, new Rectangle(0, top, glyphSize, (int)(20 * scale)), row.IsActive ? p.AccentText : p.TextSecondary, 9.5f);
            var textLeft = Glyphs.Available ? glyphSize + (int)(8 * scale) : 0;

            var free = $"{FormatBytes(row.FreeBytes)} free";
            var freeWidth = TextRenderer.MeasureText(free, Typography.Caption).Width;
            TextRenderer.DrawText(g, free, Typography.Caption, new Rectangle(Width - freeWidth, top, freeWidth, (int)(20 * scale)), p.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);

            var pathRect = new Rectangle(textLeft, top, Math.Max(0, Width - textLeft - freeWidth - (int)(8 * scale)), (int)(20 * scale));
            TextRenderer.DrawText(g, row.Path, row.IsActive ? Typography.BodyStrong : Font, pathRect, p.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);

            var barHeight = 5f * scale;
            var bar = new RectangleF(textLeft, top + (24 * scale), Width - textLeft, barHeight);
            Draw.FillRounded(g, p.Track, bar, barHeight / 2f);
            if (row.TotalBytes > 0)
            {
                var used = Math.Clamp(1f - (row.FreeBytes / (float)row.TotalBytes), 0f, 1f);
                var tone = row.StateTone is Tone.Danger or Tone.Warning ? row.StateTone : used > 0.9f ? Tone.Warning : row.IsActive ? Tone.Accent : Tone.Neutral;
                Draw.FillRounded(g, p.Fill(tone), new RectangleF(bar.X, bar.Y, Math.Max(barHeight, bar.Width * used), barHeight), barHeight / 2f);
            }

            TextRenderer.DrawText(g, row.StateText, Typography.Caption, new Rectangle(textLeft, (int)(top + (31 * scale)), Width - textLeft, (int)(16 * scale)), p.Foreground(row.StateTone),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }
}
