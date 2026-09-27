using System.ComponentModel;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>
/// Details-view ListView with owner-drawn header and rows: taller rows, hover highlight,
/// soft accent selection, no grid lines, dark scrollbars in dark mode, and a last column that
/// fills the remaining width. <see cref="CellPainter"/> can take over individual cells.
/// </summary>
public sealed class ThemedListView : ListView, IThemeAware
{
    private int _rowHeight = 34;
    private int _hoverIndex = -1;
    private ImageList? _rowSizer;

    public ThemedListView()
    {
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        GridLines = false;
        MultiSelect = false;
        HideSelection = false;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        BorderStyle = BorderStyle.None;
        DoubleBuffered = true;
        Font = Typography.Body;
        ApplyColors();
    }

    /// <summary>Row height in 96-DPI pixels.</summary>
    [DefaultValue(34)]
    public int RowHeight
    {
        get => _rowHeight;
        set
        {
            _rowHeight = value;
            UpdateRowHeight();
        }
    }

    /// <summary>
    /// Optional custom cell renderer. The row background is already painted; return true when
    /// the cell content was drawn.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<DrawListViewSubItemEventArgs, bool>? CellPainter { get; set; }

    /// <summary>Text shown when the list has no items.</summary>
    [DefaultValue("")]
    public string EmptyText { get; set; } = string.Empty;

    public void ApplyTheme()
    {
        ApplyColors();
        if (IsHandleCreated)
        {
            NativeTheme.ApplyScrollbarTheme(this, Theme.Current);
        }

        Invalidate();
    }

    public void FillLastColumn()
    {
        if (Columns.Count == 0 || !IsHandleCreated)
        {
            return;
        }

        var used = 0;
        for (var i = 0; i < Columns.Count - 1; i++)
        {
            used += Columns[i].Width;
        }

        var target = ClientSize.Width - used;
        var minimum = LogicalToDeviceUnits(60);
        var last = Columns[^1];
        if (target >= minimum && last.Width != target)
        {
            last.Width = target;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateRowHeight();
        NativeTheme.ApplyScrollbarTheme(this, Theme.Current);
        FillLastColumn();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateRowHeight();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FillLastColumn();
    }

    protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
    {
        base.OnColumnWidthChanged(e);
        if (e.ColumnIndex < Columns.Count - 1)
        {
            FillLastColumn();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = GetItemAt(e.X, e.Y)?.Index ?? -1;
        if (index != _hoverIndex)
        {
            InvalidateRow(_hoverIndex);
            _hoverIndex = index;
            InvalidateRow(_hoverIndex);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateRow(_hoverIndex);
        _hoverIndex = -1;
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        using (var background = new SolidBrush(p.Card))
        {
            g.FillRectangle(background, e.Bounds);
        }

        using (var divider = new Pen(p.Divider))
        {
            g.DrawLine(divider, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        var padding = LogicalToDeviceUnits(12);
        var bounds = new Rectangle(e.Bounds.X + padding, e.Bounds.Y, Math.Max(0, e.Bounds.Width - padding - 4), e.Bounds.Height);
        var alignment = e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        TextRenderer.DrawText(g, e.Header?.Text ?? string.Empty, Typography.CaptionStrong, bounds, p.TextSecondary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | alignment);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        var p = Theme.Current;
        var selected = e.Item?.Selected == true;
        var hovered = e.ItemIndex == _hoverIndex;
        var fill = selected ? p.AccentSoft : hovered ? p.Hover : p.Card;
        using var brush = new SolidBrush(fill);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (selected)
        {
            Draw.PrepareHighQuality(e.Graphics);
            var scale = Draw.Scale(this);
            var pillHeight = e.Bounds.Height * 0.5f;
            Draw.FillRounded(e.Graphics, p.Accent, new RectangleF(e.Bounds.X + (2 * scale), e.Bounds.Y + ((e.Bounds.Height - pillHeight) / 2f), 3 * scale, pillHeight), 1.5f * scale);
        }
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (CellPainter?.Invoke(e) == true)
        {
            return;
        }

        var p = Theme.Current;
        var padding = LogicalToDeviceUnits(12);
        var bounds = new Rectangle(e.Bounds.X + padding, e.Bounds.Y, Math.Max(0, e.Bounds.Width - padding - 4), e.Bounds.Height);
        var alignment = e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        var color = e.ColumnIndex == 0 ? p.Text : p.TextSecondary;
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, e.ColumnIndex == 0 ? Font : Font, bounds, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | alignment);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int WmPaint = 0x000F;
        if (m.Msg == WmPaint && Items.Count == 0 && !string.IsNullOrEmpty(EmptyText))
        {
            using var g = CreateGraphics();
            var headerHeight = LogicalToDeviceUnits(32);
            var bounds = new Rectangle(0, headerHeight, ClientSize.Width, Math.Max(0, ClientSize.Height - headerHeight));
            TextRenderer.DrawText(g, EmptyText, Font, bounds, Theme.Current.TextTertiary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rowSizer?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ApplyColors()
    {
        BackColor = Theme.Current.Card;
        ForeColor = Theme.Current.Text;
    }

    private void UpdateRowHeight()
    {
        var height = Math.Clamp(LogicalToDeviceUnits(_rowHeight), 16, 255);
        if (_rowSizer?.ImageSize.Height == height)
        {
            return;
        }

        var previous = _rowSizer;
        _rowSizer = new ImageList { ImageSize = new Size(1, height) };
        SmallImageList = _rowSizer;
        previous?.Dispose();
    }

    private void InvalidateRow(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            Invalidate(Items[index].Bounds);
        }
    }
}

/// <summary>Owner-drawn list box with palette colors and a pluggable item painter.</summary>
public sealed class ThemedListBox : ListBox, IThemeAware
{
    private int _itemHeightLogical = 48;

    public ThemedListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        Font = Typography.Body;
        ApplyColors();
    }

    [DefaultValue(48)]
    public int ItemHeightLogical
    {
        get => _itemHeightLogical;
        set
        {
            _itemHeightLogical = value;
            UpdateItemHeight();
        }
    }

    /// <summary>Paints an item's content; the background and selection are already drawn.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<DrawItemEventArgs, object>? ItemPainter { get; set; }

    public void ApplyTheme()
    {
        ApplyColors();
        if (IsHandleCreated)
        {
            NativeTheme.ApplyScrollbarTheme(this, Theme.Current);
        }

        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateItemHeight();
        NativeTheme.ApplyScrollbarTheme(this, Theme.Current);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateItemHeight();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var p = Theme.Current;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var background = new SolidBrush(selected ? p.AccentSoft : p.Card))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }

        if (e.Index < 0 || e.Index >= Items.Count)
        {
            return;
        }

        if (selected)
        {
            Draw.PrepareHighQuality(e.Graphics);
            var scale = Draw.Scale(this);
            var pillHeight = e.Bounds.Height * 0.45f;
            Draw.FillRounded(e.Graphics, p.Accent, new RectangleF(e.Bounds.X + (2 * scale), e.Bounds.Y + ((e.Bounds.Height - pillHeight) / 2f), 3 * scale, pillHeight), 1.5f * scale);
        }

        var item = Items[e.Index];
        if (ItemPainter != null)
        {
            ItemPainter(e, item);
            return;
        }

        var padding = LogicalToDeviceUnits(12);
        TextRenderer.DrawText(e.Graphics, GetItemText(item), Font, new Rectangle(e.Bounds.X + padding, e.Bounds.Y, e.Bounds.Width - padding, e.Bounds.Height), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private void ApplyColors()
    {
        BackColor = Theme.Current.Card;
        ForeColor = Theme.Current.Text;
    }

    private void UpdateItemHeight()
    {
        var height = Math.Clamp(LogicalToDeviceUnits(_itemHeightLogical), 16, 255);
        if (ItemHeight != height)
        {
            ItemHeight = height;
        }
    }
}
