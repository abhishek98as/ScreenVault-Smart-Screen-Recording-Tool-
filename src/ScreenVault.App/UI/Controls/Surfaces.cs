using System.ComponentModel;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>Label whose color follows a <see cref="TextTone"/> and can word-wrap inside stack layouts.</summary>
public sealed class TextLabel : Label, IThemeAware
{
    private TextTone _tone;
    private bool _wrap;

    public TextLabel()
    {
        UseMnemonic = false;
        AutoSize = true;
        ApplyTheme();
    }

    public TextLabel(string text, Font? font = null, TextTone tone = TextTone.Primary, bool wrap = false)
        : this()
    {
        Text = text;
        if (font != null)
        {
            Font = font;
        }

        _tone = tone;
        Wrap = wrap;
        ApplyTheme();
    }

    [DefaultValue(TextTone.Primary)]
    public TextTone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            ApplyTheme();
        }
    }

    /// <summary>When true the label word-wraps to the width its container gives it.</summary>
    [DefaultValue(false)]
    public bool Wrap
    {
        get => _wrap;
        set
        {
            _wrap = value;
            if (value)
            {
                AutoSize = false;
            }
        }
    }

    public void ApplyTheme() => ForeColor = Theme.Current.TextColor(_tone);

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (!_wrap || proposedSize.Width <= 1 || proposedSize.Width >= int.MaxValue / 2)
        {
            return base.GetPreferredSize(proposedSize);
        }

        return new Size(proposedSize.Width, MeasureHeight(Text, Font, proposedSize.Width, UseMnemonic));
    }

    internal static int MeasureHeight(string text, Font font, int width, bool useMnemonic = false)
    {
        if (string.IsNullOrEmpty(text) || width <= 1)
        {
            return 0;
        }

        var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
        if (!useMnemonic)
        {
            flags |= TextFormatFlags.NoPrefix;
        }

        return TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags).Height + 1;
    }
}

/// <summary>
/// Panel that stacks its children vertically at full width (in the order they were added),
/// sizing each child to its preferred height. Optionally scrollable.
/// </summary>
public class StackPanel : Panel, IThemeAware
{
    private int _spacing = 12;
    private SurfaceKind _surface = SurfaceKind.Window;
    private bool _inLayout;
    private int _contentHeight;

    public StackPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        ApplyTheme();
    }

    /// <summary>Vertical gap between children, in 96-DPI pixels.</summary>
    [DefaultValue(12)]
    public int Spacing
    {
        get => _spacing;
        set
        {
            _spacing = value;
            PerformLayout();
        }
    }

    [DefaultValue(SurfaceKind.Window)]
    public SurfaceKind Surface
    {
        get => _surface;
        set
        {
            _surface = value;
            ApplyTheme();
        }
    }

    public virtual void ApplyTheme()
    {
        BackColor = Theme.Current.Surface(_surface);
        if (AutoScroll)
        {
            NativeTheme.ApplyScrollbarTheme(this, Theme.Current);
        }

        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (!Visible || !StacksChildren)
        {
            return Size;
        }

        var width = proposedSize.Width > 1 && proposedSize.Width < int.MaxValue / 2 ? proposedSize.Width : Width;
        return new Size(width, MeasureContentHeight(width - Padding.Horizontal) + Padding.Vertical);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (AutoScroll)
        {
            NativeTheme.ApplyScrollbarTheme(this, Theme.Current);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_inLayout || !StacksChildren || !Visible)
        {
            return;
        }

        _inLayout = true;
        try
        {
            ArrangeChildren();
            if (AutoScroll)
            {
                // Showing/hiding the scrollbar changes the client width; arrange once more if so.
                var widthBefore = ClientSize.Width;
                var min = new Size(0, _contentHeight);
                if (AutoScrollMinSize != min)
                {
                    AutoScrollMinSize = min;
                }

                if (ClientSize.Width != widthBefore)
                {
                    ArrangeChildren();
                }
            }
        }
        finally
        {
            _inLayout = false;
        }

        // A nested stack (e.g. a card) whose content height changed asks its parent stack to
        // re-measure it; the parent then assigns the new height and this settles immediately.
        if (!AutoScroll && Parent is StackPanel { StacksChildren: true } && _contentHeight > 0 && _contentHeight != Height)
        {
            Parent.PerformLayout();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            // Visibility of children cannot be trusted while an ancestor is hidden, so layout is
            // deferred until the panel is actually shown.
            PerformLayout();
        }
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control!.VisibleChanged += OnChildVisibleChanged;
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        e.Control!.VisibleChanged -= OnChildVisibleChanged;
        base.OnControlRemoved(e);
    }

    /// <summary>False for containers whose children are positioned by the caller.</summary>
    protected virtual bool StacksChildren => true;

    protected int ScaledSpacing => (int)Math.Round(_spacing * Draw.Scale(this));

    /// <summary>Children taking part in the stack, in the order they were added.</summary>
    protected IEnumerable<Control> VisibleChildren()
    {
        for (var i = 0; i < Controls.Count; i++)
        {
            var child = Controls[i];
            if (child.Visible)
            {
                yield return child;
            }
        }
    }

    private void OnChildVisibleChanged(object? sender, EventArgs e)
    {
        if (Visible)
        {
            PerformLayout();
            Parent?.PerformLayout();
        }
    }

    private int MeasureContentHeight(int width)
    {
        var total = 0;
        var count = 0;
        foreach (var child in VisibleChildren())
        {
            total += ChildHeight(child, width - child.Margin.Horizontal) + child.Margin.Vertical;
            count++;
        }

        return total + (Math.Max(0, count - 1) * ScaledSpacing);
    }

    private void ArrangeChildren()
    {
        var width = ClientSize.Width - Padding.Horizontal;
        if (width <= 0)
        {
            return;
        }

        var origin = AutoScroll ? AutoScrollPosition.Y : 0;
        var y = Padding.Top + origin;
        var first = true;
        foreach (var child in VisibleChildren())
        {
            if (!first)
            {
                y += ScaledSpacing;
            }

            first = false;
            var childWidth = width - child.Margin.Horizontal;
            var height = ChildHeight(child, childWidth);
            child.SetBounds(Padding.Left + child.Margin.Left, y + child.Margin.Top, childWidth, height);
            y += height + child.Margin.Vertical;
        }

        _contentHeight = y - origin + Padding.Bottom;
    }

    private static int ChildHeight(Control child, int width)
    {
        if (child is StackPanel or SettingRow or TextLabel { Wrap: true } or FlowLayoutPanel { AutoSize: true })
        {
            return child.GetPreferredSize(new Size(Math.Max(1, width), 0)).Height;
        }

        return child.AutoSize ? child.GetPreferredSize(new Size(Math.Max(1, width), 0)).Height : child.Height;
    }
}

/// <summary>Rounded card surface; stacks children like <see cref="StackPanel"/> or lets them be placed manually.</summary>
public class CardPanel : StackPanel
{
    private int _cornerRadius = 8;
    private bool _dividers;
    private bool _manualLayout;
    private Tone _accentTone = Tone.Neutral;

    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Surface = SurfaceKind.Card;
        Padding = new Padding(16);
        Spacing = 8;
    }

    [DefaultValue(8)]
    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = value;
            Invalidate();
        }
    }

    /// <summary>Draw 1px dividers between stacked children (settings-list style).</summary>
    [DefaultValue(false)]
    public bool Dividers
    {
        get => _dividers;
        set
        {
            _dividers = value;
            Invalidate();
        }
    }

    /// <summary>When true, children keep the bounds the caller assigns (no stacking).</summary>
    [DefaultValue(false)]
    public bool ManualLayout
    {
        get => _manualLayout;
        set
        {
            _manualLayout = value;
            PerformLayout();
        }
    }

    /// <summary>A non-neutral tone tints the border (e.g. a warning card).</summary>
    [DefaultValue(Tone.Neutral)]
    public Tone AccentTone
    {
        get => _accentTone;
        set
        {
            _accentTone = value;
            ApplyTheme();
        }
    }

    public override void ApplyTheme()
    {
        BackColor = _accentTone == Tone.Neutral ? Theme.Current.Surface(Surface) : Theme.Current.Soft(_accentTone);
        Invalidate();
    }

    protected override bool StacksChildren => !_manualLayout;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.Clear(Draw.ParentBackground(this));
        Draw.PrepareHighQuality(g);

        var scale = Draw.Scale(this);
        var bounds = new RectangleF(0, 0, Width, Height);
        var radius = _cornerRadius * scale;
        Draw.FillRounded(g, BackColor, bounds, radius);
        var border = _accentTone == Tone.Neutral ? p.Border : Draw.Blend(p.Soft(_accentTone), p.Fill(_accentTone), 0.35f);
        Draw.StrokeRounded(g, border, bounds, radius);

        if (_dividers && !_manualLayout)
        {
            using var pen = new Pen(p.Divider);
            Control? previous = null;
            foreach (var child in VisibleChildren())
            {
                if (previous != null)
                {
                    var y = previous.Bottom + ((child.Top - previous.Bottom) / 2);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    g.DrawLine(pen, 0, y, Width, y);
                }

                previous = child;
            }
        }
    }
}

/// <summary>
/// Windows 11 settings-style row: optional icon, a title with a secondary description on the
/// left, and the editing control right-aligned and vertically centered.
/// </summary>
public sealed class SettingRow : Panel, IThemeAware
{
    private readonly TextLabel _title;
    private readonly TextLabel _description;
    private readonly char _glyph;
    private bool _hasDescription;

    public SettingRow(string title, string? description, Control? control, char glyph = Glyphs.None)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        _glyph = glyph;

        _title = new TextLabel(title, Typography.Body) { AutoSize = false };
        _hasDescription = !string.IsNullOrEmpty(description);
        _description = new TextLabel(description ?? string.Empty, Typography.Caption, TextTone.Secondary) { AutoSize = false, Visible = _hasDescription };
        Controls.Add(_title);
        Controls.Add(_description);

        Control = control;
        if (control != null)
        {
            Controls.Add(control);
            control.SizeChanged += (_, _) => PerformLayout();
        }

        Padding = new Padding(16, 12, 16, 12);
        Height = 56;
        AccessibleName = title;
    }

    public Control? Control { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title
    {
        get => _title.Text;
        set
        {
            _title.Text = value;
            PerformLayout();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Description
    {
        get => _description.Text;
        set
        {
            _description.Text = value;
            _hasDescription = !string.IsNullOrEmpty(value);
            _description.Visible = _hasDescription;
            Parent?.PerformLayout();
            PerformLayout();
        }
    }

    public void SetDescriptionTone(TextTone tone) => _description.Tone = tone;

    public void ApplyTheme() => Invalidate();

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = proposedSize.Width > 1 && proposedSize.Width < int.MaxValue / 2 ? proposedSize.Width : Width;
        return new Size(width, Measure(width).Height);
    }

    protected override void OnParentBackColorChanged(EventArgs e)
    {
        base.OnParentBackColorChanged(e);
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_title == null || _description == null)
            return;
        var layout = Measure(Width);
        _title.SetBounds(layout.TextLeft, layout.TitleTop, layout.TextWidth, layout.TitleHeight);
        _description.SetBounds(layout.TextLeft, layout.TitleTop + layout.TitleHeight + layout.Gap, layout.TextWidth, layout.DescriptionHeight);
        if (Control != null)
        {
            Control.Location = new Point(Width - Padding.Right - Control.Width, (Height - Control.Height) / 2);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Draw.ParentBackground(this));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_glyph != Glyphs.None)
        {
            var scale = Draw.Scale(this);
            var size = (int)(20 * scale);
            var rect = new Rectangle(Padding.Left, (Height - size) / 2, size, size);
            Glyphs.Draw(e.Graphics, _glyph, rect, Theme.Current.Text, 12f);
        }
    }

    private RowLayout Measure(int width)
    {
        var scale = Draw.Scale(this);
        var iconSpace = _glyph != Glyphs.None && Glyphs.Available ? (int)(36 * scale) : 0;
        var controlWidth = Control?.Width ?? 0;
        var controlGap = Control != null ? (int)(16 * scale) : 0;
        var textLeft = Padding.Left + iconSpace;
        var textWidth = Math.Max(40, width - textLeft - Padding.Right - controlWidth - controlGap);

        var titleHeight = TextLabel.MeasureHeight(_title.Text, _title.Font, textWidth);
        var descriptionHeight = _hasDescription ? TextLabel.MeasureHeight(_description.Text, _description.Font, textWidth) : 0;
        var gap = descriptionHeight > 0 ? (int)(2 * scale) : 0;
        var textBlock = titleHeight + gap + descriptionHeight;
        var content = Math.Max(textBlock, Control?.Height ?? 0);
        var height = Math.Max((int)(56 * scale), content + Padding.Vertical);
        var titleTop = (height - textBlock) / 2;
        return new RowLayout(textLeft, textWidth, titleTop, titleHeight, descriptionHeight, gap, height);
    }

    private readonly record struct RowLayout(int TextLeft, int TextWidth, int TitleTop, int TitleHeight, int DescriptionHeight, int Gap, int Height);
}

/// <summary>Plain panel painted with a palette surface, with optional hairline dividers.</summary>
public class SurfacePanel : Panel, IThemeAware
{
    private SurfaceKind _surface = SurfaceKind.Window;
    private bool _topDivider;
    private bool _bottomDivider;

    public SurfacePanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        ApplyTheme();
    }

    [DefaultValue(SurfaceKind.Window)]
    public SurfaceKind Surface
    {
        get => _surface;
        set
        {
            _surface = value;
            ApplyTheme();
        }
    }

    [DefaultValue(false)]
    public bool TopDivider
    {
        get => _topDivider;
        set
        {
            _topDivider = value;
            Invalidate();
        }
    }

    [DefaultValue(false)]
    public bool BottomDivider
    {
        get => _bottomDivider;
        set
        {
            _bottomDivider = value;
            Invalidate();
        }
    }

    public virtual void ApplyTheme()
    {
        BackColor = Theme.Current.Surface(_surface);
        if (AutoScroll)
        {
            NativeTheme.ApplyScrollbarTheme(this, Theme.Current);
        }

        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (AutoScroll)
        {
            NativeTheme.ApplyScrollbarTheme(this, Theme.Current);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_topDivider && !_bottomDivider)
        {
            return;
        }

        using var pen = new Pen(Theme.Current.Divider);
        if (_topDivider)
        {
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }

        if (_bottomDivider)
        {
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
}
