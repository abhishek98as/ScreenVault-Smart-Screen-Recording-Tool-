using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI;

/// <summary>
/// Themed replacement for <see cref="MessageBox"/>: icon badge, headline, wrapped message,
/// optional "don't ask again" checkbox or text input, and a footer with clearly labelled
/// buttons (primary action first). Works with or without an owner window (tray actions).
/// </summary>
public sealed class ModernDialog : ModernForm
{
    private readonly GlyphBadge _badge;
    private readonly TextLabel _headline;
    private readonly TextLabel _message;
    private readonly TextField? _input;
    private readonly ModernCheckBox? _option;
    private readonly Panel _footer;
    private readonly List<ModernButton> _buttons = [];

    private ModernDialog(
        string headline,
        string message,
        MessageBoxIcon icon,
        IReadOnlyList<(string Text, DialogResult Result, ButtonKind Kind)> buttons,
        string? optionText,
        string? inputValue,
        string? inputPlaceholder,
        bool hasOwner)
    {
        Text = "ScreenVault";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = !hasOwner;
        // Always above every app, including the window that opened it: a modal dialog hidden
        // behind its owner leaves that window disabled with no visible way out.
        TopMost = true;
        StartPosition = hasOwner ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 200);
        KeyPreview = true;

        var (glyph, tone) = icon switch
        {
            MessageBoxIcon.Error => (Glyphs.Error, Tone.Danger),
            MessageBoxIcon.Warning => (Glyphs.Warning, Tone.Warning),
            MessageBoxIcon.Question => (Glyphs.Info, Tone.Accent),
            MessageBoxIcon.Information => (Glyphs.Info, Tone.Accent),
            _ => (Glyphs.Completed, Tone.Success)
        };

        _badge = new GlyphBadge { Glyph = glyph, Tone = tone, Size = new Size(40, 40), Location = new Point(24, 24) };
        _headline = new TextLabel(headline, Typography.Subtitle) { AutoSize = false, Wrap = true };
        _message = new TextLabel(message, Typography.Body, TextTone.Secondary) { AutoSize = false, Wrap = true, Visible = !string.IsNullOrEmpty(message) };
        Controls.Add(_badge);
        Controls.Add(_headline);
        Controls.Add(_message);

        if (inputValue != null)
        {
            _input = new TextField { Text = inputValue, PlaceholderText = inputPlaceholder ?? string.Empty, Size = new Size(356, 32) };
            Controls.Add(_input);
        }

        if (!string.IsNullOrEmpty(optionText))
        {
            _option = new ModernCheckBox { Text = optionText };
            Controls.Add(_option);
        }

        _footer = new FooterPanel { Height = 64 };
        Controls.Add(_footer);

        foreach (var (text, result, kind) in buttons)
        {
            var button = new ModernButton(text, kind) { DialogResult = result, Size = new Size(100, 32) };
            _buttons.Add(button);
            _footer.Controls.Add(button);
        }

        AcceptButton = _buttons.FirstOrDefault(b => b.Kind is ButtonKind.Primary or ButtonKind.Record) ?? _buttons.FirstOrDefault();
        CancelButton = _buttons.LastOrDefault(b => b.DialogResult is DialogResult.Cancel or DialogResult.No) ?? _buttons.LastOrDefault();

        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>Value of the text input (prompt dialogs).</summary>
    public string InputValue => _input?.Text ?? string.Empty;

    /// <summary>State of the optional checkbox.</summary>
    public bool OptionChecked => _option?.Checked == true;

    /// <summary>Drop-in replacement for <c>MessageBox.Show(owner, text, caption, buttons, icon)</c>.</summary>
    public static DialogResult Show(IWin32Window? owner, string message, string headline, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        var spec = buttons switch
        {
            MessageBoxButtons.OKCancel => new[] { ("OK", DialogResult.OK, ButtonKind.Primary), ("Cancel", DialogResult.Cancel, ButtonKind.Secondary) },
            MessageBoxButtons.YesNo => new[] { ("Yes", DialogResult.Yes, ButtonKind.Primary), ("No", DialogResult.No, ButtonKind.Secondary) },
            MessageBoxButtons.YesNoCancel => new[] { ("Yes", DialogResult.Yes, ButtonKind.Primary), ("No", DialogResult.No, ButtonKind.Secondary), ("Cancel", DialogResult.Cancel, ButtonKind.Secondary) },
            MessageBoxButtons.RetryCancel => new[] { ("Retry", DialogResult.Retry, ButtonKind.Primary), ("Cancel", DialogResult.Cancel, ButtonKind.Secondary) },
            _ => new[] { ("OK", DialogResult.OK, ButtonKind.Primary) }
        };

        using var dialog = new ModernDialog(headline, message, icon, spec, null, null, null, owner != null);
        return dialog.Run(owner);
    }

    public static void Info(IWin32Window? owner, string headline, string message = "") =>
        Show(owner, message, headline, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Success(IWin32Window? owner, string headline, string message = "") =>
        Show(owner, message, headline, MessageBoxButtons.OK, MessageBoxIcon.None);

    public static void Warning(IWin32Window? owner, string headline, string message = "") =>
        Show(owner, message, headline, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void Error(IWin32Window? owner, string headline, string message = "") =>
        Show(owner, message, headline, MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>Confirmation with explicit action labels, e.g. [Stop & save] [Keep recording].</summary>
    public static bool Confirm(IWin32Window? owner, string headline, string message, string confirmText, string cancelText = "Cancel", bool destructive = false, MessageBoxIcon icon = MessageBoxIcon.Question)
    {
        return ConfirmWithOption(owner, headline, message, confirmText, cancelText, null, destructive, icon).Confirmed;
    }

    /// <summary>Confirmation with an extra checkbox (e.g. "Don't ask me again").</summary>
    public static (bool Confirmed, bool OptionChecked) ConfirmWithOption(
        IWin32Window? owner,
        string headline,
        string message,
        string confirmText,
        string cancelText,
        string? optionText,
        bool destructive = false,
        MessageBoxIcon icon = MessageBoxIcon.Question)
    {
        var spec = new[]
        {
            (confirmText, DialogResult.OK, destructive ? ButtonKind.Record : ButtonKind.Primary),
            (cancelText, DialogResult.Cancel, ButtonKind.Secondary)
        };

        using var dialog = new ModernDialog(headline, message, icon, spec, optionText, null, null, owner != null);
        var result = dialog.Run(owner);
        return (result == DialogResult.OK, dialog.OptionChecked);
    }

    /// <summary>Asks for a single line of text. Returns null when cancelled.</summary>
    public static string? Prompt(IWin32Window? owner, string headline, string message, string initialValue, string confirmText = "Save", string? placeholder = null)
    {
        var spec = new[] { (confirmText, DialogResult.OK, ButtonKind.Primary), ("Cancel", DialogResult.Cancel, ButtonKind.Secondary) };
        using var dialog = new ModernDialog(headline, message, MessageBoxIcon.Information, spec, null, initialValue ?? string.Empty, placeholder, owner != null);
        dialog._badge.Glyph = Glyphs.Rename;
        return dialog.Run(owner) == DialogResult.OK ? dialog.InputValue : null;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LayoutDialog();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        if (_input != null)
        {
            _input.Focus();
            _input.SelectAll();
        }
        else
        {
            (AcceptButton as Control)?.Focus();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutDialog();
    }

    private DialogResult Run(IWin32Window? owner) => owner != null ? ShowDialog(owner) : ShowDialog();

    private void LayoutDialog()
    {
        int S(int value) => LogicalToDeviceUnits(value);

        var width = ClientSize.Width;
        var left = S(80);
        var textWidth = width - left - S(24);
        var y = S(24);

        var headlineHeight = TextLabel.MeasureHeight(_headline.Text, _headline.Font, textWidth);
        _headline.SetBounds(left, y + S(2), textWidth, headlineHeight);
        y += headlineHeight + S(2);

        if (!string.IsNullOrEmpty(_message.Text))
        {
            y += S(6);
            var messageHeight = TextLabel.MeasureHeight(_message.Text, _message.Font, textWidth);
            _message.SetBounds(left, y, textWidth, messageHeight);
            y += messageHeight;
        }

        if (_input != null)
        {
            y += S(14);
            _input.SetBounds(left, y, textWidth, S(32));
            y += S(32);
        }

        if (_option != null)
        {
            y += S(14);
            _option.Location = new Point(left, y);
            y += _option.Height;
        }

        y = Math.Max(y, S(24) + _badge.Height) + S(24);
        _footer.SetBounds(0, y, width, S(64));

        var x = width - S(24);
        for (var i = _buttons.Count - 1; i >= 0; i--)
        {
            var button = _buttons[i];
            var preferred = button.GetPreferredSize(Size.Empty);
            button.Size = new Size(Math.Max(S(96), preferred.Width), S(32));
            x -= button.Width;
            button.Location = new Point(x, (S(64) - button.Height) / 2);
            x -= S(8);
        }

        ClientSize = new Size(width, y + S(64));
    }

    private sealed class FooterPanel : Panel, IThemeAware
    {
        public FooterPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Current.Sidebar;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Current.Divider);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }
}
