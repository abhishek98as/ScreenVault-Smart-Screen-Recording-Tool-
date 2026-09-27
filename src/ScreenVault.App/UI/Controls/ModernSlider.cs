using System.ComponentModel;
using System.Globalization;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>
/// Fluent-style horizontal slider (replaces TrackBar). The filled part of the track starts at
/// <see cref="Origin"/> so bipolar values such as gain (−20…+20 dB) read naturally.
/// Keyboard: arrows, PageUp/PageDown, Home/End. Exposed to screen readers as a slider.
/// </summary>
public sealed class ModernSlider : Control, IThemeAware
{
    private int _minimum;
    private int _maximum = 100;
    private int _value;
    private int? _origin;
    private bool _dragging;
    private bool _hover;

    public ModernSlider()
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
        Size = new Size(200, 28);
    }

    public event EventHandler? ValueChanged;

    [DefaultValue(0)]
    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            _maximum = Math.Max(_maximum, value);
            Value = _value;
            Invalidate();
        }
    }

    [DefaultValue(100)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            _minimum = Math.Min(_minimum, value);
            Value = _value;
            Invalidate();
        }
    }

    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, _minimum, _maximum);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [DefaultValue(1)]
    public int SmallChange { get; set; } = 1;

    [DefaultValue(5)]
    public int LargeChange { get; set; } = 5;

    /// <summary>Value where the filled track begins (defaults to <see cref="Minimum"/>).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int? Origin
    {
        get => _origin;
        set
        {
            _origin = value;
            Invalidate();
        }
    }

    public void ApplyTheme() => Invalidate();

    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessibleObject(this);

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Down:
                Value -= SmallChange;
                break;
            case Keys.Right:
            case Keys.Up:
                Value += SmallChange;
                break;
            case Keys.PageDown:
                Value -= LargeChange;
                break;
            case Keys.PageUp:
                Value += LargeChange;
                break;
            case Keys.Home:
                Value = _minimum;
                break;
            case Keys.End:
                Value = _maximum;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled)
        {
            return;
        }

        Focus();
        _dragging = true;
        Capture = true;
        Value = ValueFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Value = ValueFromX(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Focused && Enabled)
        {
            Value += e.Delta > 0 ? SmallChange : -SmallChange;
            if (e is HandledMouseEventArgs handled)
            {
                handled.Handled = true;
            }

            return;
        }

        base.OnMouseWheel(e);
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
        var thumb = 20f * scale;
        var trackHeight = 4f * scale;
        var left = thumb / 2f;
        var right = Width - (thumb / 2f);
        var centerY = Height / 2f;
        var track = new RectangleF(left, centerY - (trackHeight / 2f), right - left, trackHeight);
        Draw.FillRounded(g, p.IsDark ? p.BorderStrong : p.InputBorder, track, trackHeight / 2f);

        var valueX = XFromValue(_value);
        var originX = XFromValue(Math.Clamp(_origin ?? _minimum, _minimum, _maximum));
        var fillLeft = Math.Min(valueX, originX);
        var fillRight = Math.Max(valueX, originX);
        if (fillRight - fillLeft > 0.5f)
        {
            Draw.FillRounded(g, Enabled ? p.Accent : p.TextDisabled, new RectangleF(fillLeft, track.Y, fillRight - fillLeft, trackHeight), trackHeight / 2f);
        }

        if (_origin.HasValue && _origin.Value > _minimum && _origin.Value < _maximum)
        {
            var tick = new RectangleF(originX - (1 * scale), centerY - (6 * scale), 2 * scale, 12 * scale);
            Draw.FillRounded(g, p.TextTertiary, tick, 1 * scale);
        }

        var thumbRect = new RectangleF(valueX - (thumb / 2f), centerY - (thumb / 2f), thumb, thumb);
        Draw.FillCircle(g, p.Card, thumbRect);
        using (var pen = new Pen(p.Border, Math.Max(1f, scale)))
        {
            g.DrawEllipse(pen, thumbRect);
        }

        var inner = (_dragging ? 8f : _hover ? 12f : 10f) * scale;
        Draw.FillCircle(g, Enabled ? p.Accent : p.TextDisabled, new RectangleF(valueX - (inner / 2f), centerY - (inner / 2f), inner, inner));

        if (Focused && ShowFocusCues)
        {
            var ring = RectangleF.Inflate(thumbRect, 2 * scale, 2 * scale);
            Draw.FocusRing(g, this, ring, ring.Width / 2f);
        }
    }

    private float XFromValue(int value)
    {
        var thumb = 20f * Draw.Scale(this);
        var usable = Math.Max(1f, Width - thumb);
        var range = Math.Max(1, _maximum - _minimum);
        return (thumb / 2f) + (usable * (value - _minimum) / range);
    }

    private int ValueFromX(int x)
    {
        var thumb = 20f * Draw.Scale(this);
        var usable = Math.Max(1f, Width - thumb);
        var ratio = Math.Clamp((x - (thumb / 2f)) / usable, 0f, 1f);
        return _minimum + (int)Math.Round(ratio * (_maximum - _minimum));
    }

    private sealed class SliderAccessibleObject(ModernSlider owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.Slider;

        public override string? Value
        {
            get => owner.Value.ToString(CultureInfo.CurrentCulture);
            set
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed))
                {
                    owner.Value = parsed;
                }
            }
        }
    }
}
