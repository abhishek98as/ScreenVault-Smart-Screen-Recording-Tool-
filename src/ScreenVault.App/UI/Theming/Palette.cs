namespace ScreenVault.App.UI.Theming;

/// <summary>Text color roles used by labels and custom-painted controls.</summary>
public enum TextTone
{
    Primary,
    Secondary,
    Tertiary,
    Accent,
    Danger,
    Success,
    Warning,
    OnAccent
}

/// <summary>Semantic color families for buttons, pills, bars and icon badges.</summary>
public enum Tone
{
    Neutral,
    Accent,
    Success,
    Warning,
    Danger
}

/// <summary>Background layers, from the window canvas up to raised cards.</summary>
public enum SurfaceKind
{
    Window,
    Card,
    Sidebar,
    Input
}

/// <summary>
/// Complete set of design tokens for one appearance (light, dark or high contrast).
/// Colors were chosen for WCAG AA contrast of body text on their intended surfaces.
/// </summary>
public sealed class Palette
{
    public required string Name { get; init; }
    public required bool IsDark { get; init; }
    public bool IsHighContrast { get; init; }

    // Surfaces
    public required Color Window { get; init; }
    public required Color Card { get; init; }
    public required Color Sidebar { get; init; }
    public required Color Hover { get; init; }
    public required Color Pressed { get; init; }
    public required Color Border { get; init; }
    public required Color BorderStrong { get; init; }
    public required Color Divider { get; init; }

    // Text
    public required Color Text { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color TextTertiary { get; init; }
    public required Color TextDisabled { get; init; }

    // Brand accent
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentPressed { get; init; }
    public required Color AccentText { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color OnAccent { get; init; }

    // Status colors (fill + readable text + soft tint background)
    public required Color Danger { get; init; }
    public required Color DangerHover { get; init; }
    public required Color DangerPressed { get; init; }
    public required Color DangerText { get; init; }
    public required Color DangerSoft { get; init; }
    public required Color Success { get; init; }
    public required Color SuccessText { get; init; }
    public required Color SuccessSoft { get; init; }
    public required Color Warning { get; init; }
    public required Color WarningText { get; init; }
    public required Color WarningSoft { get; init; }

    // Inverse ("strong") neutral button, e.g. Stop
    public required Color Strong { get; init; }
    public required Color StrongHover { get; init; }
    public required Color OnStrong { get; init; }

    // Inputs & tracks
    public required Color InputBackground { get; init; }
    public required Color InputBorder { get; init; }
    public required Color InputBorderHover { get; init; }
    public required Color Track { get; init; }

    public Color Surface(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Card => Card,
        SurfaceKind.Sidebar => Sidebar,
        SurfaceKind.Input => InputBackground,
        _ => Window
    };

    public Color TextColor(TextTone tone) => tone switch
    {
        TextTone.Secondary => TextSecondary,
        TextTone.Tertiary => TextTertiary,
        TextTone.Accent => AccentText,
        TextTone.Danger => DangerText,
        TextTone.Success => SuccessText,
        TextTone.Warning => WarningText,
        TextTone.OnAccent => OnAccent,
        _ => Text
    };

    /// <summary>Solid fill color for a tone (used by bars, dots and badges).</summary>
    public Color Fill(Tone tone) => tone switch
    {
        Tone.Accent => Accent,
        Tone.Success => Success,
        Tone.Warning => Warning,
        Tone.Danger => Danger,
        _ => TextTertiary
    };

    /// <summary>Tinted background for a tone (used by pills and icon badges).</summary>
    public Color Soft(Tone tone) => tone switch
    {
        Tone.Accent => AccentSoft,
        Tone.Success => SuccessSoft,
        Tone.Warning => WarningSoft,
        Tone.Danger => DangerSoft,
        _ => Hover
    };

    /// <summary>Readable foreground for a tone drawn on its soft tint or on a card.</summary>
    public Color Foreground(Tone tone) => tone switch
    {
        Tone.Accent => AccentText,
        Tone.Success => SuccessText,
        Tone.Warning => WarningText,
        Tone.Danger => DangerText,
        _ => TextSecondary
    };

    public static Palette Light { get; } = new()
    {
        Name = "Light",
        IsDark = false,
        Window = Hex(0xF4F5F8),
        Card = Hex(0xFFFFFF),
        Sidebar = Hex(0xEBEDF2),
        Hover = Hex(0xEEF0F4),
        Pressed = Hex(0xE4E7EC),
        Border = Hex(0xE1E4EA),
        BorderStrong = Hex(0xC9CED7),
        Divider = Hex(0xECEEF2),
        Text = Hex(0x1A1D23),
        TextSecondary = Hex(0x5A616D),
        TextTertiary = Hex(0x858C98),
        TextDisabled = Hex(0xA8AEB8),
        Accent = Hex(0x5B5BD6),
        AccentHover = Hex(0x4F4FCB),
        AccentPressed = Hex(0x4545BA),
        AccentText = Hex(0x4646C1),
        AccentSoft = Hex(0xECECFD),
        OnAccent = Hex(0xFFFFFF),
        Danger = Hex(0xDC2626),
        DangerHover = Hex(0xC91F1F),
        DangerPressed = Hex(0xB31B1B),
        DangerText = Hex(0xC01E1E),
        DangerSoft = Hex(0xFDECEC),
        Success = Hex(0x16A34A),
        SuccessText = Hex(0x137A3A),
        SuccessSoft = Hex(0xE5F6EB),
        Warning = Hex(0xE08A00),
        WarningText = Hex(0x9A5B00),
        WarningSoft = Hex(0xFDF2DC),
        Strong = Hex(0x1F2229),
        StrongHover = Hex(0x33373F),
        OnStrong = Hex(0xFFFFFF),
        InputBackground = Hex(0xFFFFFF),
        InputBorder = Hex(0xCDD2DA),
        InputBorderHover = Hex(0xA9B0BC),
        Track = Hex(0xE3E6EB)
    };

    public static Palette Dark { get; } = new()
    {
        Name = "Dark",
        IsDark = true,
        Window = Hex(0x151619),
        Card = Hex(0x1E1F24),
        Sidebar = Hex(0x1A1B1F),
        Hover = Hex(0x2A2C32),
        Pressed = Hex(0x33353C),
        Border = Hex(0x2D2F36),
        BorderStrong = Hex(0x41444D),
        Divider = Hex(0x292B31),
        Text = Hex(0xECEDF0),
        TextSecondary = Hex(0xA7ACB6),
        TextTertiary = Hex(0x7C828E),
        TextDisabled = Hex(0x5A5F69),
        Accent = Hex(0x5B5BD6),
        AccentHover = Hex(0x6868E0),
        AccentPressed = Hex(0x5050C8),
        AccentText = Hex(0xA9A9F8),
        AccentSoft = Hex(0x2A2A4D),
        OnAccent = Hex(0xFFFFFF),
        Danger = Hex(0xDC2626),
        DangerHover = Hex(0xE53A3A),
        DangerPressed = Hex(0xC21F1F),
        DangerText = Hex(0xFF8A8A),
        DangerSoft = Hex(0x3D1D20),
        Success = Hex(0x22A95A),
        SuccessText = Hex(0x6EDB9B),
        SuccessSoft = Hex(0x183226),
        Warning = Hex(0xF0A020),
        WarningText = Hex(0xFFC66B),
        WarningSoft = Hex(0x3A2F16),
        Strong = Hex(0xECEDF0),
        StrongHover = Hex(0xFFFFFF),
        OnStrong = Hex(0x15161A),
        InputBackground = Hex(0x25272D),
        InputBorder = Hex(0x3C3F48),
        InputBorderHover = Hex(0x565A65),
        Track = Hex(0x33353D)
    };

    /// <summary>Palette built from the Windows high-contrast system colors (accessibility).</summary>
    public static Palette HighContrast()
    {
        var window = SystemColors.Window;
        var text = SystemColors.WindowText;
        var highlight = SystemColors.Highlight;
        var highlightText = SystemColors.HighlightText;
        var gray = SystemColors.GrayText;
        return new Palette
        {
            Name = "HighContrast",
            IsDark = window.GetBrightness() < 0.5f,
            IsHighContrast = true,
            Window = window,
            Card = window,
            Sidebar = window,
            Hover = highlight,
            Pressed = highlight,
            Border = text,
            BorderStrong = text,
            Divider = text,
            Text = text,
            TextSecondary = text,
            TextTertiary = text,
            TextDisabled = gray,
            Accent = highlight,
            AccentHover = highlight,
            AccentPressed = highlight,
            AccentText = SystemColors.HotTrack,
            AccentSoft = window,
            OnAccent = highlightText,
            Danger = highlight,
            DangerHover = highlight,
            DangerPressed = highlight,
            DangerText = text,
            DangerSoft = window,
            Success = highlight,
            SuccessText = text,
            SuccessSoft = window,
            Warning = highlight,
            WarningText = text,
            WarningSoft = window,
            Strong = highlight,
            StrongHover = highlight,
            OnStrong = highlightText,
            InputBackground = window,
            InputBorder = text,
            InputBorderHover = highlight,
            Track = gray
        };
    }

    private static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
