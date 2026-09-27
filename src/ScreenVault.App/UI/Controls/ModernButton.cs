using System.ComponentModel;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

public enum ButtonKind
{
    /// <summary>Neutral outlined button (default).</summary>
    Secondary,

    /// <summary>Filled brand-accent button for the main action of a view.</summary>
    Primary,

    /// <summary>Borderless button that only shows a fill on hover (toolbars, icon buttons).</summary>
    Subtle,

    /// <summary>Borderless red text button for destructive actions such as Delete.</summary>
    Destructive,

    /// <summary>Filled red button used to start a recording.</summary>
    Record,

    /// <summary>Filled high-contrast neutral button (inverse), used for Stop.</summary>
    Strong
}

/// <summary>
/// Fluent-style push button with rounded corners, hover/pressed states, an optional icon glyph
/// and a visible keyboard focus ring. It is still a real <see cref="Button"/>, so AcceptButton,
/// DialogResult, keyboard activation and accessibility keep working.
/// </summary>
public class ModernButton : Button, IThemeAware
{
    private ButtonKind _kind = ButtonKind.Secondary;
    private char _glyph = Glyphs.None;
    private Color _glyphColor = Color.Empty;
    private int _cornerRadius = 6;
    private bool _hover;
    private bool _pressed;

    public ModernButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseMnemonic = false;
        Font = Typography.Body;
        Size = new Size(96, 32);
    }

    public ModernButton(string text, ButtonKind kind = ButtonKind.Secondary, char glyph = Glyphs.None)
        : this()
    {
        Text = text;
        _kind = kind;
        _glyph = glyph;
        if (kind is ButtonKind.Primary or ButtonKind.Record or ButtonKind.Strong)
        {
            Font = Typography.BodyStrong;
        }
    }

    [DefaultValue(ButtonKind.Secondary)]
    public ButtonKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Invalidate();
        }
    }

    [DefaultValue(Glyphs.None)]
    public char Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Invalidate();
        }
    }

    /// <summary>Optional glyph color (e.g. a red square on a neutral Stop button).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GlyphColor
    {
        get => _glyphColor;
        set
        {
            _glyphColor = value;
            Invalidate();
        }
    }

    [DefaultValue(6)]
    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = value;
            Invalidate();
        }
    }

    public void ApplyTheme() => Invalidate();

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        if (AutoSize)
        {
            // Preferred sizes are already computed for the current DPI; don't scale them twice.
            Size = GetPreferredSize(Size.Empty);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var scale = Draw.Scale(this);
        var height = (int)Math.Ceiling(32 * scale);
        if (string.IsNullOrEmpty(Text))
        {
            return new Size(height, height);
        }

        var textSize = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFlags());
        var glyphWidth = HasGlyph ? (int)Math.Ceiling((GlyphPointSize * DeviceDpi / 72f) + (8 * scale)) : 0;
        var width = textSize.Width + glyphWidth + (int)Math.Ceiling(28 * scale);
        return new Size(Math.Max(width, (int)(64 * scale)), height);
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
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        base.OnEnabledChanged(e);
        Invalidate();
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
        // Everything is painted in OnPaint to avoid flicker.
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        var palette = Theme.Current;
        g.Clear(Draw.ParentBackground(this));
        Draw.PrepareHighQuality(g);

        var scale = Draw.Scale(this);
        var bounds = new RectangleF(0, 0, Width, Height);
        var radius = _cornerRadius * scale;
        var (fill, border, fore) = ResolveColors(palette);

        Draw.FillRounded(g, fill, bounds, radius);
        if (border.A > 0)
        {
            Draw.StrokeRounded(g, border, bounds, radius);
        }

        PaintContent(g, fore, scale);

        if (Focused && ShowFocusCues)
        {
            Draw.FocusRing(g, this, bounds, radius);
        }
    }

    private bool HasGlyph => _glyph != Glyphs.None && Glyphs.Available;

    private float GlyphPointSize => Font.SizeInPoints + 1.5f;

    private void PaintContent(Graphics g, Color fore, float scale)
    {
        var text = Text;
        var hasText = !string.IsNullOrEmpty(text);
        var glyphColor = !Enabled || _glyphColor.IsEmpty ? fore : _glyphColor;
        var glyphPx = (int)Math.Ceiling(GlyphPointSize * DeviceDpi / 72f);

        if (!hasText)
        {
            Glyphs.Draw(g, _glyph, ClientRectangle, glyphColor, GlyphPointSize);
            return;
        }

        var flags = TextFlags();
        var textSize = TextRenderer.MeasureText(g, text, Font, Size.Empty, flags);
        var gap = HasGlyph ? (int)(8 * scale) : 0;
        var contentWidth = textSize.Width + (HasGlyph ? glyphPx + gap : 0);
        var padding = (int)(14 * scale);

        int x = TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft
            ? padding
            : Math.Max(padding / 2, (Width - contentWidth) / 2);

        if (HasGlyph)
        {
            Glyphs.Draw(g, _glyph, new Rectangle(x, 0, glyphPx, Height), glyphColor, GlyphPointSize);
            x += glyphPx + gap;
        }

        var textRect = new Rectangle(x, 0, Math.Max(0, Width - x - (padding / 2)), Height);
        TextRenderer.DrawText(g, text, Font, textRect, fore, flags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private TextFormatFlags TextFlags()
    {
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        if (!UseMnemonic)
        {
            flags |= TextFormatFlags.NoPrefix;
        }
        else if (!ShowKeyboardCues)
        {
            flags |= TextFormatFlags.HidePrefix;
        }

        return flags;
    }

    private (Color Fill, Color Border, Color Fore) ResolveColors(Palette p)
    {
        var transparent = Color.Transparent;
        if (!Enabled)
        {
            return _kind switch
            {
                ButtonKind.Primary or ButtonKind.Record or ButtonKind.Strong => (p.Track, transparent, p.TextDisabled),
                ButtonKind.Secondary => (p.Card, p.Border, p.TextDisabled),
                _ => (transparent, transparent, p.TextDisabled)
            };
        }

        return _kind switch
        {
            ButtonKind.Primary => (_pressed ? p.AccentPressed : _hover ? p.AccentHover : p.Accent, transparent, p.OnAccent),
            ButtonKind.Record => (_pressed ? p.DangerPressed : _hover ? p.DangerHover : p.Danger, transparent, p.OnAccent),
            ButtonKind.Strong => (_hover || _pressed ? p.StrongHover : p.Strong, transparent, p.OnStrong),
            ButtonKind.Subtle => (_pressed ? p.Pressed : _hover ? p.Hover : transparent, transparent, p.Text),
            ButtonKind.Destructive => (_pressed ? Draw.Blend(p.DangerSoft, p.Danger, 0.15f) : _hover ? p.DangerSoft : transparent, transparent, p.DangerText),
            _ => (_pressed ? p.Pressed : _hover ? p.Hover : p.Card, _hover ? p.BorderStrong : p.Border, p.Text)
        };
    }
}
