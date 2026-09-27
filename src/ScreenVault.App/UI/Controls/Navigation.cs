using System.ComponentModel;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>Base for single-selection item strips (sidebar navigation and segmented tabs).</summary>
public abstract class SelectorControl : Control, IThemeAware
{
    private readonly List<(char Glyph, string Text)> _items = [];
    private int _selectedIndex = -1;

    protected SelectorControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);
        TabStop = true;
        Font = Typography.Body;
    }

    public event EventHandler? SelectedIndexChanged;

    [Browsable(false)]
    public int Count => _items.Count;

    [DefaultValue(-1)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
            if (clamped == _selectedIndex)
            {
                return;
            }

            _selectedIndex = clamped;
            Invalidate();
            if (clamped >= 0)
            {
                AccessibilityNotifyClients(AccessibleEvents.Selection, clamped);
                AccessibilityNotifyClients(AccessibleEvents.Focus, clamped);
            }

            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected int HoverIndex { get; private set; } = -1;

    protected abstract AccessibleRole ContainerRole { get; }

    protected abstract AccessibleRole ItemRole { get; }

    public void AddItem(string text, char glyph = Glyphs.None)
    {
        _items.Add((glyph, text));
        if (_selectedIndex < 0)
        {
            _selectedIndex = 0;
        }

        Invalidate();
    }

    public string ItemText(int index) => _items[index].Text;

    public void ApplyTheme() => Invalidate();

    /// <summary>Bounds of an item in client coordinates.</summary>
    public abstract Rectangle ItemBounds(int index);

    protected char ItemGlyph(int index) => _items[index].Glyph;

    protected override AccessibleObject CreateAccessibilityInstance() => new SelectorAccessibleObject(this);

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Up:
            case Keys.Left:
                SelectedIndex = Math.Max(0, _selectedIndex - 1);
                break;
            case Keys.Down:
            case Keys.Right:
                SelectedIndex = Math.Min(_items.Count - 1, _selectedIndex + 1);
                break;
            case Keys.Home:
                SelectedIndex = 0;
                break;
            case Keys.End:
                SelectedIndex = _items.Count - 1;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (hit != HoverIndex)
        {
            HoverIndex = hit;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        HoverIndex = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var hit = HitTest(e.Location);
        if (hit >= 0 && e.Button == MouseButtons.Left)
        {
            Focus();
            SelectedIndex = hit;
        }
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

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
    }

    private int HitTest(Point point)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ItemBounds(i).Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class SelectorAccessibleObject(SelectorControl owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => owner.ContainerRole;

        public override int GetChildCount() => owner.Count;

        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner.Count ? new ItemAccessibleObject(owner, this, index) : null;
    }

    private sealed class ItemAccessibleObject(SelectorControl owner, AccessibleObject parent, int index) : AccessibleObject
    {
        public override string? Name => owner.ItemText(index);

        public override AccessibleRole Role => owner.ItemRole;

        public override AccessibleObject? Parent => parent;

        public override Rectangle Bounds => owner.RectangleToScreen(owner.ItemBounds(index));

        public override string? DefaultAction => "Select";

        public override AccessibleStates State
        {
            get
            {
                var state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                if (owner.SelectedIndex == index)
                {
                    state |= AccessibleStates.Selected;
                    if (owner.Focused)
                    {
                        state |= AccessibleStates.Focused;
                    }
                }

                return state;
            }
        }

        public override void DoDefaultAction() => owner.SelectedIndex = index;

        public override void Select(AccessibleSelection flags)
        {
            if ((flags & (AccessibleSelection.TakeSelection | AccessibleSelection.TakeFocus)) != 0)
            {
                owner.SelectedIndex = index;
            }
        }
    }
}

/// <summary>Windows 11 settings-style vertical navigation (icon + label, accent indicator).</summary>
public sealed class NavigationList : SelectorControl
{
    protected override AccessibleRole ContainerRole => AccessibleRole.PageTabList;

    protected override AccessibleRole ItemRole => AccessibleRole.PageTab;

    public override Rectangle ItemBounds(int index)
    {
        var scale = Draw.Scale(this);
        var height = (int)(38 * scale);
        var gap = (int)(2 * scale);
        return new Rectangle((int)(4 * scale), index * (height + gap), Width - (int)(8 * scale), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.Clear(Draw.ParentBackground(this));
        Draw.PrepareHighQuality(g);
        var scale = Draw.Scale(this);

        for (var i = 0; i < Count; i++)
        {
            var bounds = ItemBounds(i);
            var selected = i == SelectedIndex;
            if (selected || i == HoverIndex)
            {
                Draw.FillRounded(g, selected ? p.Hover : Draw.Blend(p.Sidebar, p.Hover, 0.6f), bounds, 5 * scale);
            }

            if (selected)
            {
                var pill = new RectangleF(bounds.X, bounds.Y + (bounds.Height * 0.28f), 3 * scale, bounds.Height * 0.44f);
                Draw.FillRounded(g, p.Accent, pill, 1.5f * scale);
                if (Focused && ShowFocusCues)
                {
                    Draw.FocusRing(g, this, bounds, 5 * scale);
                }
            }

            var glyph = ItemGlyph(i);
            var textLeft = bounds.X + (int)(14 * scale);
            if (glyph != Glyphs.None && Glyphs.Available)
            {
                Glyphs.Draw(g, glyph, new Rectangle(textLeft, bounds.Y, (int)(18 * scale), bounds.Height), selected ? p.AccentText : p.Text, 11f);
                textLeft += (int)(30 * scale);
            }

            TextRenderer.DrawText(g, ItemText(i), selected ? Typography.BodyStrong : Font, new Rectangle(textLeft, bounds.Y, bounds.Right - textLeft, bounds.Height), p.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>Pill-shaped segmented tabs (e.g. Markers | Parts | Timeline).</summary>
public sealed class SegmentedControl : SelectorControl
{
    public SegmentedControl()
    {
        Size = new Size(300, 34);
    }

    protected override AccessibleRole ContainerRole => AccessibleRole.PageTabList;

    protected override AccessibleRole ItemRole => AccessibleRole.PageTab;

    public override Rectangle ItemBounds(int index)
    {
        if (Count == 0)
        {
            return Rectangle.Empty;
        }

        var inset = (int)(3 * Draw.Scale(this));
        var width = (Width - (inset * 2)) / (float)Count;
        return new Rectangle(inset + (int)(index * width), inset, (int)width, Height - (inset * 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.Clear(Draw.ParentBackground(this));
        Draw.PrepareHighQuality(g);
        var scale = Draw.Scale(this);

        var track = new RectangleF(0, 0, Width, Height);
        Draw.FillRounded(g, p.IsDark ? p.Window : p.Sidebar, track, 7 * scale);
        Draw.StrokeRounded(g, p.Border, track, 7 * scale);

        for (var i = 0; i < Count; i++)
        {
            var bounds = ItemBounds(i);
            var selected = i == SelectedIndex;
            if (selected)
            {
                Draw.FillRounded(g, p.Card, bounds, 5 * scale);
                Draw.StrokeRounded(g, p.Border, bounds, 5 * scale);
                if (Focused && ShowFocusCues)
                {
                    Draw.FocusRing(g, this, bounds, 5 * scale);
                }
            }
            else if (i == HoverIndex)
            {
                Draw.FillRounded(g, Draw.WithAlpha(p.Hover, 160), bounds, 5 * scale);
            }

            TextRenderer.DrawText(g, ItemText(i), selected ? Typography.BodyStrong : Font, bounds, selected ? p.Text : p.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
