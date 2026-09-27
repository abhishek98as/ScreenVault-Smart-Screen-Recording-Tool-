using System.Runtime.InteropServices;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>
/// Drop-down list styled like the other inputs. The closed state is painted completely by us
/// (the native combo box ignores custom colors for its border and arrow); the open list is
/// owner-drawn with the palette. Keyboard, accessibility and the native popup are unchanged.
/// </summary>
public sealed class ModernComboBox : ComboBox, IThemeAware
{
    private const int WmPaint = 0x000F;
    private bool _hover;

    public ModernComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        IntegralHeight = false;
        MaxDropDownItems = 12;
        Font = Typography.Body;
        Width = 220;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Current.Card;
        ForeColor = Theme.Current.Text;
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateItemHeight();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateItemHeight();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);
        Invalidate();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if ((e.State & DrawItemState.ComboBoxEdit) != 0)
        {
            // The closed field is painted in WM_PAINT; the native control may still ask us to draw
            // the selection outside of it (e.g. keyboard selection), so just schedule a repaint.
            Invalidate();
            return;
        }

        if (e.Index < 0 || e.Index >= Items.Count)
        {
            return;
        }

        var p = Theme.Current;
        var g = e.Graphics;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var background = new SolidBrush(selected ? p.Hover : p.Card))
        {
            g.FillRectangle(background, e.Bounds);
        }

        var scale = Draw.Scale(this);
        if (selected)
        {
            Draw.PrepareHighQuality(g);
            var pillHeight = e.Bounds.Height * 0.5f;
            Draw.FillRounded(g, p.Accent, new RectangleF(e.Bounds.X + (3 * scale), e.Bounds.Y + ((e.Bounds.Height - pillHeight) / 2f), 3 * scale, pillHeight), 1.5f * scale);
        }

        var textRect = new Rectangle(e.Bounds.X + (int)(12 * scale), e.Bounds.Y, e.Bounds.Width - (int)(16 * scale), e.Bounds.Height);
        TextRenderer.DrawText(g, GetItemText(Items[e.Index]), Font, textRect, p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPaint && DropDownStyle == ComboBoxStyle.DropDownList)
        {
            PaintClosedState(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    private void UpdateItemHeight()
    {
        var height = LogicalToDeviceUnits(26);
        if (ItemHeight != height)
        {
            ItemHeight = height;
        }
    }

    private void PaintClosedState(ref Message m)
    {
        var ps = default(PaintStruct);
        var usesOwnDc = m.WParam == IntPtr.Zero;
        var hdc = usesOwnDc ? BeginPaint(Handle, ref ps) : m.WParam;
        try
        {
            using var target = Graphics.FromHdc(hdc);
            using var buffer = BufferedGraphicsManager.Current.Allocate(target, ClientRectangle);
            Render(buffer.Graphics);
            buffer.Render(target);
        }
        finally
        {
            if (usesOwnDc)
            {
                EndPaint(Handle, ref ps);
            }
        }

        m.Result = IntPtr.Zero;
    }

    private void Render(Graphics g)
    {
        var p = Theme.Current;
        g.Clear(Draw.ParentBackground(this));
        Draw.PrepareHighQuality(g);

        var scale = Draw.Scale(this);
        var bounds = new RectangleF(0, 0, Width, Height);
        var radius = 6 * scale;
        var focused = Focused || DroppedDown;
        Draw.FillRounded(g, !Enabled ? p.Hover : _hover ? Draw.Blend(p.InputBackground, p.Hover, 0.5f) : p.InputBackground, bounds, radius);
        Draw.StrokeRounded(g, focused ? p.BorderStrong : _hover && Enabled ? p.InputBorderHover : p.InputBorder, bounds, radius);

        if (focused)
        {
            using var clip = Draw.RoundedRect(bounds, radius);
            var state = g.Save();
            g.SetClip(clip);
            using var accent = new SolidBrush(p.Accent);
            var line = Math.Max(2f, 2f * scale);
            g.FillRectangle(accent, 0, Height - line, Width, line);
            g.Restore(state);
        }

        var chevronWidth = (int)(28 * scale);
        var text = SelectedIndex >= 0 ? GetItemText(SelectedItem) : Text;
        var textRect = new Rectangle((int)(10 * scale), 0, Math.Max(0, Width - chevronWidth - (int)(10 * scale)), Height);
        TextRenderer.DrawText(g, text, Font, textRect, Enabled ? p.Text : p.TextDisabled,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        var chevronRect = new Rectangle(Width - chevronWidth, 0, chevronWidth - (int)(4 * scale), Height);
        var chevronColor = Enabled ? p.TextSecondary : p.TextDisabled;
        if (Glyphs.Available)
        {
            Glyphs.Draw(g, Glyphs.ChevronDown, chevronRect, chevronColor, 7.5f);
        }
        else
        {
            var cx = chevronRect.X + (chevronRect.Width / 2f);
            var cy = Height / 2f;
            using var pen = new Pen(chevronColor, Math.Max(1f, 1.3f * scale));
            g.DrawLines(pen, [new PointF(cx - (4 * scale), cy - (2 * scale)), new PointF(cx, cy + (2 * scale)), new PointF(cx + (4 * scale), cy - (2 * scale))]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Restore;
        public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);
}
