using System.ComponentModel;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>
/// Thin rounded progress/usage bar. Supports determinate values, an indeterminate
/// (marquee) animation, and a fixed or automatic tone.
/// </summary>
public sealed class ModernProgressBar : Control, IThemeAware
{
    private readonly System.Windows.Forms.Timer _marqueeTimer;
    private int _value;
    private int _maximum = 100;
    private bool _marquee;
    private float _marqueePhase;
    private Tone _tone = Tone.Accent;

    public ModernProgressBar()
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
        Size = new Size(200, 6);
        AccessibleRole = AccessibleRole.ProgressBar;
        _marqueeTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _marqueeTimer.Tick += (_, _) =>
        {
            _marqueePhase = (_marqueePhase + 0.018f) % 1.4f;
            Invalidate();
        };
    }

    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, _maximum);
            if (clamped != _value)
            {
                _value = clamped;
                Invalidate();
            }
        }
    }

    [DefaultValue(100)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(1, value);
            _value = Math.Min(_value, _maximum);
            Invalidate();
        }
    }

    [DefaultValue(Tone.Accent)]
    public Tone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            Invalidate();
        }
    }

    /// <summary>Indeterminate animation (e.g. while finalizing a recording).</summary>
    [DefaultValue(false)]
    public bool Marquee
    {
        get => _marquee;
        set
        {
            _marquee = value;
            if (value && Visible)
            {
                _marqueeTimer.Start();
            }
            else
            {
                _marqueeTimer.Stop();
            }

            Invalidate();
        }
    }

    public void ApplyTheme() => Invalidate();

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (_marquee && Visible)
        {
            _marqueeTimer.Start();
        }
        else
        {
            _marqueeTimer.Stop();
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

        var barHeight = Math.Min(Height, Math.Max(3f, 6f * Draw.Scale(this)));
        var track = new RectangleF(0, (Height - barHeight) / 2f, Width, barHeight);
        var radius = barHeight / 2f;
        Draw.FillRounded(g, p.Track, track, radius);

        var fill = p.Fill(_tone);
        if (_marquee)
        {
            var segment = Width * 0.35f;
            var x = (_marqueePhase * Width) - segment;
            var state = g.Save();
            using var clip = Draw.RoundedRect(track, radius);
            g.SetClip(clip);
            Draw.FillRounded(g, fill, new RectangleF(x, track.Y, segment, barHeight), radius);
            g.Restore(state);
            return;
        }

        if (_value > 0)
        {
            var width = Math.Max(barHeight, Width * (_value / (float)_maximum));
            Draw.FillRounded(g, fill, new RectangleF(0, track.Y, width, barHeight), radius);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _marqueeTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Small rounded label with a colored dot, e.g. "● Recording", "Healthy", "Low space".</summary>
public sealed class StatusPill : Control, IThemeAware
{
    private Tone _tone = Tone.Neutral;
    private bool _showDot = true;
    private float _dotOpacity = 1f;

    public StatusPill()
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
        Font = Typography.CaptionStrong;
        AutoSize = true;
        AccessibleRole = AccessibleRole.StaticText;
    }

    [DefaultValue(Tone.Neutral)]
    public Tone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            Invalidate();
        }
    }

    [DefaultValue(true)]
    public bool ShowDot
    {
        get => _showDot;
        set
        {
            _showDot = value;
            PerformLayoutAndInvalidate();
        }
    }

    /// <summary>0..1, lets the owner animate a "live" pulse without extra timers.</summary>
    [DefaultValue(1f)]
    public float DotOpacity
    {
        get => _dotOpacity;
        set
        {
            _dotOpacity = Math.Clamp(value, 0f, 1f);
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
        var textWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        var dot = _showDot ? (int)(14 * scale) : 0;
        return new Size(textWidth + dot + (int)(16 * scale), (int)Math.Ceiling(22 * scale));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        PerformLayoutAndInvalidate();
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
        Draw.FillRounded(g, p.Soft(_tone), bounds, Height / 2f);

        var x = (int)(8 * scale);
        if (_showDot)
        {
            var dot = 7f * scale;
            var color = Draw.WithAlpha(p.Fill(_tone), (int)(255 * (0.35f + (0.65f * _dotOpacity))));
            Draw.FillCircle(g, color, new RectangleF(x, (Height - dot) / 2f, dot, dot));
            x += (int)(14 * scale);
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), p.Foreground(_tone),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private void PerformLayoutAndInvalidate()
    {
        if (AutoSize)
        {
            Size = GetPreferredSize(Size.Empty);
        }

        Invalidate();
    }
}

/// <summary>An icon glyph centered in a tinted circle (dialog icons, success marks, empty states).</summary>
public sealed class GlyphBadge : Control, IThemeAware
{
    private char _glyph = Glyphs.Info;
    private Tone _tone = Tone.Accent;
    private bool _filled;

    public GlyphBadge()
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
        Size = new Size(40, 40);
    }

    [DefaultValue(Glyphs.Info)]
    public char Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Invalidate();
        }
    }

    [DefaultValue(Tone.Accent)]
    public Tone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            Invalidate();
        }
    }

    /// <summary>Solid tone fill with a white glyph instead of the soft tint.</summary>
    [DefaultValue(false)]
    public bool Filled
    {
        get => _filled;
        set
        {
            _filled = value;
            Invalidate();
        }
    }

    public void ApplyTheme() => Invalidate();

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.Clear(Draw.ParentBackground(this));
        Draw.PrepareHighQuality(g);

        var size = Math.Min(Width, Height) - 1;
        var circle = new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size);
        Draw.FillCircle(g, _filled ? p.Fill(_tone) : p.Soft(_tone), circle);
        var points = size * 72f / DeviceDpi * 0.45f;
        Glyphs.Draw(g, _glyph, Rectangle.Round(circle), _filled ? p.OnAccent : p.Foreground(_tone), points);
    }
}

/// <summary>A bare icon glyph (no background), e.g. next to a device name.</summary>
public sealed class GlyphIcon : Control, IThemeAware
{
    private char _glyph = Glyphs.Info;
    private TextTone _tone = TextTone.Secondary;
    private float _pointSize = 10.5f;

    public GlyphIcon()
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
        Size = new Size(20, 20);
    }

    [DefaultValue(Glyphs.Info)]
    public char Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Invalidate();
        }
    }

    [DefaultValue(TextTone.Secondary)]
    public TextTone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            Invalidate();
        }
    }

    [DefaultValue(10.5f)]
    public float PointSize
    {
        get => _pointSize;
        set
        {
            _pointSize = value;
            Invalidate();
        }
    }

    public void ApplyTheme() => Invalidate();

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Draw.ParentBackground(this));
        Glyphs.Draw(e.Graphics, _glyph, ClientRectangle, Enabled ? Theme.Current.TextColor(_tone) : Theme.Current.TextDisabled, _pointSize);
    }
}
