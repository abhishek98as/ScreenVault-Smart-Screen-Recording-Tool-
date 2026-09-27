using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI;

/// <summary>
/// Base for the bottom-right prompts (meeting detected, reminder): borderless card with an icon,
/// title, message, action buttons and a countdown bar. Shown without taking focus so it never
/// interrupts typing in a meeting app; dismisses itself when the countdown ends.
/// </summary>
public abstract class ToastForm : ModernForm
{
    private readonly System.Windows.Forms.Timer _countdownTimer;
    private readonly ModernProgressBar _countdown;
    private readonly FlowLayoutPanel _buttons;
    private readonly TextLabel _message;
    private readonly int _totalSeconds;
    private int _remainingSeconds;

    protected ToastForm(char glyph, string title, string message, int seconds = 30)
        : base(WindowChrome.Borderless)
    {
        _totalSeconds = seconds;
        _remainingSeconds = seconds;

        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(420, 148);

        var badge = new GlyphBadge { Glyph = glyph, Tone = Tone.Accent, Bounds = new Rectangle(18, 18, 40, 40) };
        var titleLabel = new TextLabel(title, Typography.BodyStrong)
        {
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(70, 18, 300, 20)
        };
        _message = new TextLabel(message, Typography.Caption, TextTone.Secondary)
        {
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(70, 40, 330, 18)
        };

        var btnClose = new ModernButton(string.Empty, ButtonKind.Subtle, Glyphs.Close)
        {
            Bounds = new Rectangle(380, 10, 28, 28),
            AccessibleName = "Dismiss"
        };
        btnClose.Click += (_, _) =>
        {
            OnDismissed();
            CloseToast();
        };

        _buttons = new FlowLayoutPanel
        {
            Bounds = new Rectangle(70, 76, 340, 40),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Current.Window
        };

        _countdown = new ModernProgressBar
        {
            Bounds = new Rectangle(0, 142, 420, 6),
            Maximum = seconds * 10,
            Value = seconds * 10,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };

        Controls.AddRange([badge, titleLabel, _message, btnClose, _buttons, _countdown]);
        EnableDrag(this);

        ResumeLayout(false);
        PerformLayout();

        _countdownTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _countdownTimer.Tick += OnCountdownTick;
        Shown += (_, _) => _countdownTimer.Start();
        PositionAtBottomRight();
    }

    protected override bool ShowWithoutActivation => true;

    protected ModernButton AddAction(string text, ButtonKind kind, Action onClick, char glyph = Glyphs.None)
    {
        ArgumentNullException.ThrowIfNull(onClick);
        var button = new ModernButton(text, kind, glyph) { Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0) };
        button.Size = button.GetPreferredSize(Size.Empty);
        button.Click += (_, _) =>
        {
            CloseToast();
            onClick();
        };
        _buttons.Controls.Add(button);
        return button;
    }

    /// <summary>Called when the user closes the toast with the ✕ button.</summary>
    protected virtual void OnDismissed()
    {
    }

    protected void SetMessage(string message) => _message.Text = message;

    protected override void OnThemeApplied()
    {
        base.OnThemeApplied();
        _buttons.BackColor = Theme.Current.Window;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _countdownTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _countdown.Value = Math.Max(0, _countdown.Value - 1);
        if (_countdown.Value % 10 == 0)
        {
            _remainingSeconds = _countdown.Value / 10;
            AccessibleDescription = $"Closes in {_remainingSeconds} seconds";
        }

        if (_countdown.Value <= 0)
        {
            CloseToast();
        }
    }

    private void CloseToast()
    {
        _countdownTimer.Stop();
        Close();
    }

    private void PositionAtBottomRight()
    {
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(this);
        var margin = LogicalToDeviceUnits(12);
        Location = new Point(workingArea.Right - Width - margin, workingArea.Bottom - Height - margin);
    }

    /// <summary>Total countdown length in seconds.</summary>
    protected int TotalSeconds => _totalSeconds;
}
