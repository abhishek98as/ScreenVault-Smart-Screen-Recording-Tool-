using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>Borderless native TextBox whose colors follow the input palette.</summary>
internal sealed class InnerTextBox : TextBox
{
    public InnerTextBox()
    {
        BorderStyle = BorderStyle.None;
        ApplyColors();
    }

    public void ApplyColors()
    {
        BackColor = Theme.Current.InputBackground;
        ForeColor = Theme.Current.Text;
    }
}

/// <summary>
/// Single-line text input in a rounded Fluent shell: subtle border, stronger border on hover
/// and an accent underline while focused. Optional leading icon (e.g. search).
/// </summary>
public class TextField : Control, IThemeAware
{
    private readonly InnerTextBox _box;
    private char _leadingGlyph = Glyphs.None;
    private bool _hover;

    public TextField()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        SetStyle(ControlStyles.Selectable, false);

        _box = new InnerTextBox();
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        _box.MouseEnter += (_, _) => SetHover(true);
        _box.MouseLeave += (_, _) => SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position)));
        _box.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _box.KeyDown += (_, e) => OnKeyDown(e);
        Controls.Add(_box);

        Font = Typography.Body;
        Size = new Size(240, 32);
        Padding = new Padding(10, 0, 10, 0);
    }

    /// <summary>The hosted native text box (for selection, key handling, etc.).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TextBox Inner => _box;

    [AllowNull]
    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value ?? string.Empty;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText
    {
        get => _box.PlaceholderText;
        set => _box.PlaceholderText = value;
    }

    [DefaultValue(false)]
    public bool ReadOnly
    {
        get => _box.ReadOnly;
        set
        {
            _box.ReadOnly = value;
            Invalidate();
        }
    }

    [DefaultValue(Glyphs.None)]
    public char LeadingGlyph
    {
        get => _leadingGlyph;
        set
        {
            _leadingGlyph = value;
            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>Extra space reserved on the right for derived controls (spinners, suffixes).</summary>
    protected virtual int TrailingWidth => 0;

    protected bool IsFocusedWithin => _box.Focused;

    public virtual void ApplyTheme()
    {
        _box.ApplyColors();
        Invalidate();
    }

    public void SelectAll() => _box.SelectAll();

    public new void Focus() => _box.Focus();

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _box.Font = Font;
        PerformLayout();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _box.Enabled = Enabled;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var scale = Draw.Scale(this);
        var left = Padding.Left + (_leadingGlyph != Glyphs.None && Glyphs.Available ? (int)(24 * scale) : 0);
        var width = Math.Max(10, Width - left - Padding.Right - TrailingWidth);
        _box.SetBounds(left, Math.Max(0, (Height - _box.PreferredHeight) / 2) + 1, width, _box.PreferredHeight);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        SetHover(true);
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        SetHover(false);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_box.Focused)
        {
            _box.Focus();
        }
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

        var scale = Draw.Scale(this);
        var bounds = new RectangleF(0, 0, Width, Height);
        var radius = 6 * scale;
        var focused = _box.Focused;
        Draw.FillRounded(g, Enabled ? p.InputBackground : p.Hover, bounds, radius);
        Draw.StrokeRounded(g, focused ? p.BorderStrong : _hover && Enabled ? p.InputBorderHover : p.InputBorder, bounds, radius);

        if (focused)
        {
            // Fluent-style accent underline, clipped to the rounded shape.
            using var clip = Draw.RoundedRect(bounds, radius);
            var state = g.Save();
            g.SetClip(clip);
            var lineHeight = Math.Max(2f, 2f * scale);
            using var brush = new SolidBrush(p.Accent);
            g.FillRectangle(brush, 0, Height - lineHeight, Width, lineHeight);
            g.Restore(state);
        }

        if (_leadingGlyph != Glyphs.None)
        {
            var size = (int)(16 * scale);
            Glyphs.Draw(g, _leadingGlyph, new Rectangle(Padding.Left - (int)(2 * scale), (Height - size) / 2, size + (int)(4 * scale), size), p.TextSecondary, 10f);
        }

        PaintTrailing(g, scale);
    }

    /// <summary>Paints the area reserved by <see cref="TrailingWidth"/>.</summary>
    protected virtual void PaintTrailing(Graphics g, float scale)
    {
    }

    private void SetHover(bool hover)
    {
        if (_hover != hover)
        {
            _hover = hover;
            Invalidate();
        }
    }
}

/// <summary>
/// Numeric input with a unit suffix and up/down chevrons (replaces NumericUpDown, whose native
/// spin buttons cannot be themed). Supports arrow keys, mouse wheel and press-and-hold.
/// </summary>
public sealed class NumberField : TextField
{
    private readonly System.Windows.Forms.Timer _repeat;
    private decimal _value;
    private decimal _minimum;
    private decimal _maximum = 100;
    private decimal _increment = 1;
    private int _decimalPlaces;
    private string _suffix = string.Empty;
    private int _repeatDirection;
    private int _hoverPart;

    public NumberField()
    {
        Size = new Size(120, 32);
        Inner.TextAlign = HorizontalAlignment.Left;
        Inner.KeyDown += OnInnerKeyDown;
        Inner.Leave += (_, _) => CommitText();
        _repeat = new System.Windows.Forms.Timer { Interval = 400 };
        _repeat.Tick += (_, _) =>
        {
            _repeat.Interval = 60;
            Step(_repeatDirection);
        };
        UpdateText();
    }

    public event EventHandler? ValueChanged;

    [DefaultValue(typeof(decimal), "0")]
    public decimal Value
    {
        get => _value;
        set => SetValue(value, raise: true);
    }

    [DefaultValue(typeof(decimal), "0")]
    public decimal Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_maximum < value)
            {
                _maximum = value;
            }

            SetValue(_value, raise: false);
        }
    }

    [DefaultValue(typeof(decimal), "100")]
    public decimal Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            if (_minimum > value)
            {
                _minimum = value;
            }

            SetValue(_value, raise: false);
        }
    }

    [DefaultValue(typeof(decimal), "1")]
    public decimal Increment
    {
        get => _increment;
        set => _increment = value <= 0 ? 1 : value;
    }

    [DefaultValue(0)]
    public int DecimalPlaces
    {
        get => _decimalPlaces;
        set
        {
            _decimalPlaces = Math.Clamp(value, 0, 6);
            UpdateText();
        }
    }

    /// <summary>Unit shown after the number, e.g. "s", "ms" or "days".</summary>
    [DefaultValue("")]
    public string Suffix
    {
        get => _suffix;
        set
        {
            _suffix = value ?? string.Empty;
            PerformLayout();
            Invalidate();
        }
    }

    protected override int TrailingWidth
    {
        get
        {
            var scale = Draw.Scale(this);
            var suffix = string.IsNullOrEmpty(_suffix) ? 0 : TextRenderer.MeasureText(_suffix, Font).Width + (int)(4 * scale);
            return (int)(26 * scale) + suffix;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var part = HitSpinner(e.Location);
        if (part != _hoverPart)
        {
            _hoverPart = part;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverPart = 0;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var part = HitSpinner(e.Location);
        if (part != 0 && e.Button == MouseButtons.Left && Enabled)
        {
            CommitText();
            _repeatDirection = part == 1 ? 1 : -1;
            Step(_repeatDirection);
            _repeat.Interval = 400;
            _repeat.Start();
            Inner.Focus();
            return;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _repeat.Stop();
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Only react while focused, so scrolling a page never changes values by accident.
        if (Enabled && IsFocusedWithin)
        {
            Step(e.Delta > 0 ? 1 : -1);
            if (e is HandledMouseEventArgs handled)
            {
                handled.Handled = true;
            }

            return;
        }

        base.OnMouseWheel(e);
    }

    protected override void PaintTrailing(Graphics g, float scale)
    {
        var p = Theme.Current;
        var spinnerWidth = (int)(26 * scale);
        var spinnerLeft = Width - spinnerWidth - (int)(2 * scale);

        if (!string.IsNullOrEmpty(_suffix))
        {
            var suffixWidth = TextRenderer.MeasureText(_suffix, Font).Width;
            var rect = new Rectangle(spinnerLeft - suffixWidth - (int)(2 * scale), 0, suffixWidth, Height);
            TextRenderer.DrawText(g, _suffix, Font, rect, p.TextSecondary, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        var half = Height / 2;
        var up = new Rectangle(spinnerLeft, (int)(3 * scale), spinnerWidth - (int)(2 * scale), half - (int)(3 * scale));
        var down = new Rectangle(spinnerLeft, half, spinnerWidth - (int)(2 * scale), half - (int)(3 * scale));
        if (_hoverPart == 1 && Enabled)
        {
            Draw.FillRounded(g, p.Hover, up, 3 * scale);
        }
        else if (_hoverPart == 2 && Enabled)
        {
            Draw.FillRounded(g, p.Hover, down, 3 * scale);
        }

        var color = Enabled ? p.TextSecondary : p.TextDisabled;
        if (Glyphs.Available)
        {
            Glyphs.Draw(g, Glyphs.ChevronUp, up, color, 6.5f);
            Glyphs.Draw(g, Glyphs.ChevronDown, down, color, 6.5f);
        }
        else
        {
            DrawChevron(g, up, color, pointingUp: true, scale);
            DrawChevron(g, down, color, pointingUp: false, scale);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _repeat.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void DrawChevron(Graphics g, Rectangle r, Color color, bool pointingUp, float scale)
    {
        var cx = r.X + (r.Width / 2f);
        var cy = r.Y + (r.Height / 2f);
        var w = 3.5f * scale;
        var h = 2f * scale;
        using var pen = new Pen(color, Math.Max(1f, 1.2f * scale));
        if (pointingUp)
        {
            g.DrawLines(pen, [new PointF(cx - w, cy + h), new PointF(cx, cy - h), new PointF(cx + w, cy + h)]);
        }
        else
        {
            g.DrawLines(pen, [new PointF(cx - w, cy - h), new PointF(cx, cy + h), new PointF(cx + w, cy - h)]);
        }
    }

    private int HitSpinner(Point location)
    {
        var spinnerWidth = (int)(26 * Draw.Scale(this));
        if (location.X < Width - spinnerWidth - 2 || location.X > Width)
        {
            return 0;
        }

        return location.Y < Height / 2 ? 1 : 2;
    }

    private void OnInnerKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up:
                Step(1);
                e.Handled = e.SuppressKeyPress = true;
                break;
            case Keys.Down:
                Step(-1);
                e.Handled = e.SuppressKeyPress = true;
                break;
            case Keys.Enter:
                CommitText();
                break;
        }
    }

    private void Step(int direction)
    {
        CommitText();
        SetValue(_value + (direction * _increment), raise: true);
        Inner.SelectionStart = Inner.TextLength;
    }

    private void CommitText()
    {
        var text = Inner.Text.Trim();
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed) ||
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
        {
            SetValue(parsed, raise: true);
        }
        else
        {
            UpdateText();
        }
    }

    private void SetValue(decimal value, bool raise)
    {
        var clamped = Math.Round(Math.Clamp(value, _minimum, _maximum), _decimalPlaces, MidpointRounding.AwayFromZero);
        var changed = clamped != _value;
        _value = clamped;
        UpdateText();
        if (changed && raise)
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateText()
    {
        var formatted = _value.ToString("F" + _decimalPlaces.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
        if (Inner.Text != formatted)
        {
            Inner.Text = formatted;
        }
    }
}
