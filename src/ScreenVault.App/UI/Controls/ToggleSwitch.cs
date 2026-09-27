using System.ComponentModel;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>
/// Windows 11 style on/off switch. Derives from <see cref="CheckBox"/> so existing code keeps
/// using <c>Checked</c>/<c>CheckedChanged</c>, and keyboard (Space) plus screen readers work.
/// Shows its label to the right, or an "On"/"Off" state label to the left when it has no text.
/// </summary>
public sealed class ToggleSwitch : CheckBox, IThemeAware
{
    private readonly System.Windows.Forms.Timer _animation;
    private float _knob;
    private bool _hover;

    public ToggleSwitch()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        UseMnemonic = false;
        AutoSize = true;
        Font = Typography.Body;
        _animation = new System.Windows.Forms.Timer { Interval = 15 };
        _animation.Tick += OnAnimationTick;
    }

    /// <summary>Show "On"/"Off" to the left of the switch (used when the row title is the label).</summary>
    [DefaultValue(true)]
    public bool ShowStateText { get; set; } = true;

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
        var trackWidth = (int)Math.Ceiling(40 * scale);
        var height = (int)Math.Ceiling(28 * scale);
        var label = LabelText;
        if (string.IsNullOrEmpty(label))
        {
            return new Size(trackWidth + 4, height);
        }

        var textWidth = TextRenderer.MeasureText(label, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        if (string.IsNullOrEmpty(Text))
        {
            // Reserve room for the wider of "On"/"Off" so the switch does not jump.
            textWidth = Math.Max(
                TextRenderer.MeasureText("Off", Font, Size.Empty, TextFormatFlags.SingleLine).Width,
                TextRenderer.MeasureText("On", Font, Size.Empty, TextFormatFlags.SingleLine).Width);
        }

        return new Size(trackWidth + textWidth + (int)(12 * scale) + 4, height);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        if (IsHandleCreated && Visible)
        {
            _animation.Start();
        }
        else
        {
            _knob = Checked ? 1f : 0f;
        }

        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _knob = Checked ? 1f : 0f;
    }

    protected override void OnMouseEnter(EventArgs eventargs)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(eventargs);
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(eventargs);
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

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        var p = Theme.Current;
        g.Clear(Draw.ParentBackground(this));
        Draw.PrepareHighQuality(g);

        var scale = Draw.Scale(this);
        var trackW = 40 * scale;
        var trackH = 20 * scale;
        var hasText = !string.IsNullOrEmpty(Text);
        var trackX = hasText ? 2f : Width - trackW - 2f;
        var track = new RectangleF(trackX, (Height - trackH) / 2f, trackW, trackH);

        var on = _knob >= 0.5f;
        Color trackFill;
        Color trackBorder;
        Color knobColor;
        if (!Enabled)
        {
            trackFill = Checked ? p.TextDisabled : Color.Transparent;
            trackBorder = p.TextDisabled;
            knobColor = Checked ? p.Card : p.TextDisabled;
        }
        else if (on)
        {
            trackFill = _hover ? p.AccentHover : p.Accent;
            trackBorder = trackFill;
            knobColor = p.OnAccent;
        }
        else
        {
            trackFill = _hover ? p.Hover : Color.Transparent;
            trackBorder = p.TextSecondary;
            knobColor = p.TextSecondary;
        }

        Draw.FillRounded(g, trackFill, track, trackH / 2f);
        Draw.StrokeRounded(g, trackBorder, track, trackH / 2f, Math.Max(1f, scale));

        var knobSize = (_hover && Enabled ? 14f : 12f) * scale;
        var inset = (trackH - knobSize) / 2f;
        var travel = trackW - (2f * inset) - knobSize;
        var knobX = track.X + inset + (travel * _knob);
        var knobY = track.Y + inset;
        Draw.FillCircle(g, knobColor, new RectangleF(knobX, knobY, knobSize, knobSize));

        var label = LabelText;
        if (!string.IsNullOrEmpty(label))
        {
            var gap = (int)(12 * scale);
            var textRect = hasText
                ? new Rectangle((int)(track.Right + gap), 0, Math.Max(0, Width - (int)track.Right - gap), Height)
                : new Rectangle(0, 0, Math.Max(0, (int)track.X - gap), Height);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis |
                        (hasText ? TextFormatFlags.Left : TextFormatFlags.Right);
            TextRenderer.DrawText(g, label, Font, textRect, Enabled ? p.Text : p.TextDisabled, flags);
        }

        if (Focused && ShowFocusCues)
        {
            var ring = RectangleF.Inflate(track, 3 * scale, 3 * scale);
            Draw.FocusRing(g, this, ring, ring.Height / 2f);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Dispose();
        }

        base.Dispose(disposing);
    }

    private string LabelText => !string.IsNullOrEmpty(Text) ? Text : ShowStateText ? (Checked ? "On" : "Off") : string.Empty;

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var target = Checked ? 1f : 0f;
        var step = 0.2f;
        _knob = _knob < target ? Math.Min(target, _knob + step) : Math.Max(target, _knob - step);
        if (Math.Abs(_knob - target) < 0.001f)
        {
            _knob = target;
            _animation.Stop();
        }

        Invalidate();
    }
}

/// <summary>Rounded, accent-filled checkbox for inline options (e.g. "Don't ask again").</summary>
public sealed class ModernCheckBox : CheckBox, IThemeAware
{
    private bool _hover;

    public ModernCheckBox()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        UseMnemonic = false;
        AutoSize = true;
        Font = Typography.Body;
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
        return new Size((int)(28 * scale) + textWidth + 2, (int)Math.Ceiling(24 * scale));
    }

    protected override void OnMouseEnter(EventArgs eventargs)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(eventargs);
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(eventargs);
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

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        var p = Theme.Current;
        g.Clear(Draw.ParentBackground(this));
        Draw.PrepareHighQuality(g);

        var scale = Draw.Scale(this);
        var size = 18 * scale;
        var box = new RectangleF(2, (Height - size) / 2f, size, size);
        var radius = 4 * scale;

        if (Checked)
        {
            var fill = !Enabled ? p.TextDisabled : _hover ? p.AccentHover : p.Accent;
            Draw.FillRounded(g, fill, box, radius);
            using var pen = new Pen(p.OnAccent, Math.Max(1.5f, 1.8f * scale))
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };
            g.DrawLines(pen,
            [
                new PointF(box.X + (size * 0.25f), box.Y + (size * 0.52f)),
                new PointF(box.X + (size * 0.43f), box.Y + (size * 0.70f)),
                new PointF(box.X + (size * 0.76f), box.Y + (size * 0.32f))
            ]);
        }
        else
        {
            Draw.FillRounded(g, _hover && Enabled ? p.Hover : p.InputBackground, box, radius);
            Draw.StrokeRounded(g, Enabled ? p.TextSecondary : p.TextDisabled, box, radius, Math.Max(1f, scale));
        }

        var textRect = new Rectangle((int)(box.Right + (8 * scale)), 0, Math.Max(0, Width - (int)(box.Right + (8 * scale))), Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? p.Text : p.TextDisabled,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues)
        {
            Draw.FocusRing(g, this, RectangleF.Inflate(box, 2 * scale, 2 * scale), radius + (2 * scale));
        }
    }
}
