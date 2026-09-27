using System.ComponentModel;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>
/// Records a global shortcut: focus it and press a key combination. Shows the result as key caps
/// (e.g. [Ctrl] [Alt] [Shift] [M]). Produces strings in the "Ctrl+Alt+Shift+M" format that the
/// hotkey service parses. Esc cancels; a modifier (or an F-key) is required.
/// </summary>
public sealed class HotkeyField : Control, IThemeAware
{
    private string _hotkey = string.Empty;
    private Keys _pendingModifiers;
    private bool _hover;
    private bool _invalidAttempt;
    private bool _readOnly;

    public HotkeyField()
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
        Font = Typography.Body;
        Size = new Size(240, 32);
        AccessibleRole = AccessibleRole.HotkeyField;
        Cursor = Cursors.Hand;
    }

    public event EventHandler? HotkeyChanged;

    /// <summary>Display-only key caps (not focusable, no border), e.g. on the wizard's summary page.</summary>
    [DefaultValue(false)]
    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            SetStyle(ControlStyles.Selectable, !value);
            TabStop = !value;
            Cursor = value ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    [DefaultValue("")]
    public string Hotkey
    {
        get => _hotkey;
        set
        {
            var normalized = value ?? string.Empty;
            if (_hotkey == normalized)
            {
                return;
            }

            _hotkey = normalized;
            AccessibleDescription = DisplayText(normalized);
            Invalidate();
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyTheme() => Invalidate();

    /// <summary>Formats a hotkey string for display, e.g. "Ctrl + Alt + Shift + 1".</summary>
    public static string DisplayText(string hotkey)
    {
        return string.Join(" + ", Parts(hotkey));
    }

    protected override bool IsInputKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        return key != Keys.Tab || (keyData & (Keys.Control | Keys.Alt)) != 0 || base.IsInputKey(keyData);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused || _readOnly)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;

        if (key == Keys.Tab && (modifiers == Keys.None || modifiers == Keys.Shift))
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        if (key == Keys.Escape && modifiers == Keys.None)
        {
            _pendingModifiers = Keys.None;
            _invalidAttempt = false;
            Invalidate();
            return true;
        }

        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
        {
            _pendingModifiers = modifiers;
            _invalidAttempt = false;
            Invalidate();
            return true;
        }

        var isFunctionKey = key >= Keys.F1 && key <= Keys.F24;
        if ((modifiers & (Keys.Control | Keys.Alt | Keys.Shift)) == Keys.None && !isFunctionKey)
        {
            _invalidAttempt = true;
            Invalidate();
            return true;
        }

        var parts = new List<string>(4);
        if ((modifiers & Keys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & Keys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & Keys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        parts.Add(key.ToString());
        _pendingModifiers = Keys.None;
        _invalidAttempt = false;
        Hotkey = string.Join("+", parts);
        Invalidate();
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _pendingModifiers = e.Modifiers;
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
        _pendingModifiers = Keys.None;
        _invalidAttempt = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_readOnly)
        {
            Focus();
        }
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
        if (!_readOnly)
        {
            Draw.FillRounded(g, p.InputBackground, bounds, radius);
            Draw.StrokeRounded(g, Focused ? p.Accent : _hover ? p.InputBorderHover : p.InputBorder, bounds, radius, Focused ? Math.Max(1.5f, 1.5f * scale) : 1f);
        }

        var x = _readOnly ? 0 : (int)(8 * scale);
        if (Focused && _pendingModifiers == Keys.None)
        {
            var hint = _invalidAttempt ? "Add Ctrl, Alt or Shift" : "Press a shortcut…";
            TextRenderer.DrawText(g, hint, Font, new Rectangle(x + (int)(2 * scale), 0, Width - x, Height), _invalidAttempt ? p.WarningText : p.TextTertiary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return;
        }

        var parts = (Focused ? ModifierParts(_pendingModifiers) : Parts(_hotkey)).ToList();
        var capHeight = (int)(22 * scale);
        var capTop = (Height - capHeight) / 2;
        var gap = (int)(4 * scale);
        var widths = parts
            .Select(part => Math.Max(capHeight, TextRenderer.MeasureText(part, Typography.CaptionStrong, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + (int)(12 * scale)))
            .ToList();
        if (_readOnly)
        {
            // Right-align so the caps line up at the edge of a settings row.
            x = Math.Max(0, Width - (widths.Sum() + (gap * Math.Max(0, widths.Count - 1))));
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var capWidth = widths[i];
            if (x + capWidth > Width - (_readOnly ? 0 : 4 * scale))
            {
                break;
            }

            var cap = new RectangleF(x, capTop, capWidth, capHeight);
            Draw.FillRounded(g, p.Hover, cap, 4 * scale);
            Draw.StrokeRounded(g, p.Border, cap, 4 * scale);
            TextRenderer.DrawText(g, part, Typography.CaptionStrong, Rectangle.Round(cap), p.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            x += capWidth + gap;
        }
    }

    private static List<string> ModifierParts(Keys modifiers)
    {
        var parts = new List<string>(3);
        if ((modifiers & Keys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & Keys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & Keys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        return parts;
    }

    private static IEnumerable<string> Parts(string hotkey)
    {
        foreach (var raw in hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            yield return FriendlyKeyName(raw);
        }
    }

    private static string FriendlyKeyName(string name)
    {
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
        {
            return name[1..];
        }

        return name switch
        {
            "Control" => "Ctrl",
            "Oemcomma" => ",",
            "OemPeriod" => ".",
            "OemMinus" => "-",
            "Oemplus" => "=",
            "OemQuestion" => "/",
            "OemSemicolon" => ";",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "Oemtilde" => "`",
            "OemQuotes" => "'",
            "OemPipe" => "\\",
            "Next" => "PgDn",
            "Prior" => "PgUp",
            "Return" => "Enter",
            _ => name
        };
    }
}
