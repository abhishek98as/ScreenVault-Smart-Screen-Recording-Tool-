using System.ComponentModel;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI;

/// <summary>
/// Live audio level meter: a rounded bar (green → amber above −12 dB → red above −3 dB)
/// with a decaying peak-hold tick and the numeric level on the right.
/// </summary>
public sealed class VuMeterControl : Control, IThemeAware
{
    private const float FloorDb = -60f;

    private float _peakL = FloorDb;
    private float _peakR = FloorDb;
    private float _rmsL = FloorDb;
    private float _rmsR = FloorDb;
    private float _heldPeakL = FloorDb;
    private float _heldPeakR = FloorDb;
    private DateTime _lastPeakDecay = DateTime.UtcNow;
    private DateTime _lastAccessibleUpdate = DateTime.MinValue;
    private bool _isMuted;

    public VuMeterControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);

        DoubleBuffered = true;
        TabStop = false;
        Font = Typography.Caption;
        Height = 20;
        Width = 160;
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted != value)
            {
                _isMuted = value;
                Invalidate();
            }
        }
    }

    /// <summary>Latest peak level in dBFS (−60 = silence).</summary>
    [Browsable(false)]
    public float CurrentPeakDb => Math.Max(_peakL, _peakR);

    public void ApplyTheme() => Invalidate();

    public void SetLevels(float peakL, float peakR, float rmsL, float rmsR)
    {
        _peakL = Math.Clamp(peakL, FloorDb, 0f);
        _peakR = Math.Clamp(peakR, FloorDb, 0f);
        _rmsL = Math.Clamp(rmsL, FloorDb, 0f);
        _rmsR = Math.Clamp(rmsR, FloorDb, 0f);

        if (_peakL > _heldPeakL)
        {
            _heldPeakL = _peakL;
        }

        if (_peakR > _heldPeakR)
        {
            _heldPeakR = _peakR;
        }

        // Peak-hold decay (~20 dB/s)
        var now = DateTime.UtcNow;
        var elapsed = (float)(now - _lastPeakDecay).TotalSeconds;
        _lastPeakDecay = now;

        var decay = elapsed * 20f;
        _heldPeakL = Math.Max(FloorDb, _heldPeakL - decay);
        _heldPeakR = Math.Max(FloorDb, _heldPeakR - decay);

        // Update accessibility name at ~1 Hz rate
        if ((now - _lastAccessibleUpdate).TotalSeconds >= 1.0)
        {
            _lastAccessibleUpdate = now;
            AccessibleDescription = LevelText();
        }

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
        var textWidth = (int)(48 * scale);
        var barWidth = Width - textWidth - (int)(8 * scale);
        if (barWidth <= 10 || Height <= 4)
        {
            return;
        }

        var barHeight = Math.Min(Height - 2, 8f * scale);
        var bar = new RectangleF(0, (Height - barHeight) / 2f, barWidth, barHeight);
        var radius = barHeight / 2f;
        Draw.FillRounded(g, p.Track, bar, radius);

        var rms = Math.Max(_rmsL, _rmsR);
        var level = Fraction(rms);
        if (level > 0.005f && !_isMuted)
        {
            var fillWidth = Math.Max(barHeight, bar.Width * level);
            var color = rms > -3f ? p.Danger : rms > -12f ? p.Warning : p.Success;
            Draw.FillRounded(g, color, new RectangleF(bar.X, bar.Y, fillWidth, barHeight), radius);
        }

        var held = Fraction(Math.Max(_heldPeakL, _heldPeakR));
        if (held > 0.01f && !_isMuted)
        {
            var x = bar.X + Math.Max(radius, (bar.Width * held) - (1.5f * scale));
            Draw.FillRounded(g, p.IsDark ? p.Text : p.TextSecondary, new RectangleF(x, bar.Y - (1 * scale), 2f * scale, barHeight + (2 * scale)), 1f * scale);
        }

        var textRect = new Rectangle(barWidth + (int)(8 * scale), 0, textWidth, Height);
        if (_isMuted)
        {
            TextRenderer.DrawText(g, "Muted", Typography.CaptionStrong, textRect, p.WarningText,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return;
        }

        var peak = Math.Max(_peakL, _peakR);
        TextRenderer.DrawText(g, LevelText(), Font, textRect, peak > -3f ? p.DangerText : p.TextSecondary,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private static float Fraction(float db) => Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);

    // "−23 dB" or "silent" (never "−∞")
    private string LevelText()
    {
        var peak = Math.Max(_peakL, _peakR);
        return peak <= -58f ? "silent" : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{peak:F0} dB");
    }
}
