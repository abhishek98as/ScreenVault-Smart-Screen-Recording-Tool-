using System.Drawing.Drawing2D;
using System.Globalization;

namespace ScreenVault.App.UI;

public sealed class VuMeterControl : Control
{
    private float _peakL = -60f;
    private float _peakR = -60f;
    private float _rmsL = -60f;
    private float _rmsR = -60f;
    private float _heldPeakL = -60f;
    private float _heldPeakR = -60f;
    private DateTime _lastPeakDecay = DateTime.UtcNow;
    private DateTime _lastAccessibleUpdate = DateTime.MinValue;
    private bool _isMuted;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
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

    public VuMeterControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);

        DoubleBuffered = true;
        Height = 22;
        Width = 160;
    }

    public void SetLevels(float peakL, float peakR, float rmsL, float rmsR)
    {
        _peakL = Math.Clamp(peakL, -60f, 0f);
        _peakR = Math.Clamp(peakR, -60f, 0f);
        _rmsL = Math.Clamp(rmsL, -60f, 0f);
        _rmsR = Math.Clamp(rmsR, -60f, 0f);

        if (_peakL > _heldPeakL) _heldPeakL = _peakL;
        if (_peakR > _heldPeakR) _heldPeakR = _peakR;

        // Peak decay (~20 dB/s)
        var now = DateTime.UtcNow;
        var elapsed = (float)(now - _lastPeakDecay).TotalSeconds;
        _lastPeakDecay = now;

        var decay = elapsed * 20f;
        _heldPeakL = Math.Max(-60f, _heldPeakL - decay);
        _heldPeakR = Math.Max(-60f, _heldPeakR - decay);

        // Update accessibility name at ~1 Hz rate
        if ((now - _lastAccessibleUpdate).TotalSeconds >= 1.0)
        {
            _lastAccessibleUpdate = now;
            var maxPeak = Math.Max(_peakL, _peakR);
            var text = maxPeak <= -58f ? "silent" : $"{maxPeak:F0} dB";
            AccessibleName = $"{AccessibleName ?? "Audio level"}: {text}";
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;

        var w = ClientRectangle.Width - 48; // Leave space for numeric dB text
        var h = ClientRectangle.Height;

        if (w <= 10 || h <= 4)
        {
            return;
        }

        // Draw light gray track background (#E0E0E0, never black)
        using var bgBrush = new SolidBrush(Color.FromArgb(224, 224, 224));
        g.FillRectangle(bgBrush, 0, 0, w, h);

        // Subtle 1px track border
        using var borderPen = new Pen(Color.FromArgb(200, 200, 200));
        g.DrawRectangle(borderPen, 0, 0, w - 1, h - 1);

        var barH = (h - 4) / 2;

        DrawChannelBar(g, 1, 1, w - 2, barH, _rmsL, _heldPeakL);
        DrawChannelBar(g, 1, 2 + barH, w - 2, barH, _rmsR, _heldPeakR);

        // Muted overlay if mic muted
        if (_isMuted)
        {
            using var muteOverlay = new SolidBrush(Color.FromArgb(180, 255, 140, 0));
            g.FillRectangle(muteOverlay, 1, 1, w - 2, h - 2);
            using var muteFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            using var muteBrush = new SolidBrush(Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("MUTED", muteFont, muteBrush, new RectangleF(1, 1, w - 2, h - 2), sf);
        }

        // Numeric text: "-23 dB" or "silent" (never "-∞")
        var maxPeak = Math.Max(_peakL, _peakR);
        var text = maxPeak <= -58f ? "silent" : $"{maxPeak:F0} dB";
        using var font = new Font("Segoe UI", 7.5f);
        var textColor = maxPeak > -3f ? Color.FromArgb(220, 53, 69) : Color.FromArgb(70, 70, 70);
        using var textBrush = new SolidBrush(textColor);
        var format = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(text, font, textBrush, new RectangleF(w + 2, 0, 46, h), format);
    }

    private static void DrawChannelBar(Graphics g, int x, int y, int w, int h, float rmsDb, float peakDb)
    {
        // -60 dB to 0 dB mapped to [0, w]
        var rmsFraction = Math.Clamp((rmsDb + 60f) / 60f, 0f, 1f);
        var peakFraction = Math.Clamp((peakDb + 60f) / 60f, 0f, 1f);

        var rmsWidth = (int)(w * rmsFraction);
        var peakX = (int)(w * peakFraction);

        if (rmsWidth > 0)
        {
            // Zone thresholds: green up to -12 dB, yellow -12..-3 dB, red above -3 dB
            var yellowX = (int)(w * 48f / 60f); // -12 dB (48/60)
            var redX = (int)(w * 57f / 60f);    // -3 dB (57/60)

            var greenPart = Math.Min(rmsWidth, yellowX);
            if (greenPart > 0)
            {
                using var greenBrush = new SolidBrush(Color.FromArgb(40, 167, 69));
                g.FillRectangle(greenBrush, x, y, greenPart, h);
            }

            if (rmsWidth > yellowX)
            {
                var yellowPart = Math.Min(rmsWidth - yellowX, redX - yellowX);
                using var yellowBrush = new SolidBrush(Color.FromArgb(255, 193, 7));
                g.FillRectangle(yellowBrush, x + yellowX, y, yellowPart, h);
            }

            if (rmsWidth > redX)
            {
                var redPart = rmsWidth - redX;
                using var redBrush = new SolidBrush(Color.FromArgb(220, 53, 69));
                g.FillRectangle(redBrush, x + redX, y, redPart, h);
            }
        }

        // Thin dark peak-hold tick line
        if (peakX > 0 && peakX <= w)
        {
            using var peakPen = new Pen(Color.FromArgb(40, 40, 40), 1.5f);
            g.DrawLine(peakPen, x + peakX - 1, y, x + peakX - 1, y + h - 1);
        }
    }
}
