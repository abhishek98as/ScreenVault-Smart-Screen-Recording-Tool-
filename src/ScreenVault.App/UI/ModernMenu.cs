using ScreenVault.App.Platform;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI;

/// <summary>Non-clickable status line at the top of a menu (keeps full contrast, unlike disabled items).</summary>
public sealed class MenuHeaderItem : ToolStripMenuItem
{
    public MenuHeaderItem(string text, bool emphasized = false, Color? dotColor = null)
        : base(text)
    {
        Emphasized = emphasized;
        DotColor = dotColor;
        Enabled = false;
    }

    public bool Emphasized { get; }

    public Color? DotColor { get; }
}

/// <summary>Applies the Fluent look to ContextMenuStrips (tray menu, split-button menus).</summary>
public static class ModernMenu
{
    public static void Apply(ContextMenuStrip menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        menu.Renderer = new ModernMenuRenderer();
        menu.Font = Typography.Body;
        menu.ShowImageMargin = true;
        menu.Padding = new Padding(4, 6, 4, 6);
        menu.ImageScalingSize = new Size(16, 16);
        menu.HandleCreated += (_, _) =>
        {
            NativeTheme.ApplyWindowFrame(menu.Handle, Theme.Current, WindowCorners.RoundSmall);
            if (CaptureExclusion.ExcludeFromCapture)
            {
                CaptureExclusion.Exclude(menu.Handle);
            }
        };
        menu.Opening += (_, _) =>
        {
            Theme.Refresh();
            ApplyItemPadding(menu.Items);
            RefreshIcons(menu.Items);
            if (menu.IsHandleCreated)
            {
                NativeTheme.ApplyWindowFrame(menu.Handle, Theme.Current, WindowCorners.RoundSmall);
                if (CaptureExclusion.ExcludeFromCapture)
                {
                    CaptureExclusion.Exclude(menu.Handle);
                }
            }
        };
    }

    /// <summary>Creates a menu item whose icon is re-rendered in the current palette each time the menu opens.</summary>
    public static ToolStripMenuItem Item(string text, char glyph, EventHandler? onClick, string? shortcut = null, Color? glyphColor = null)
    {
        return new ToolStripMenuItem(text, null, onClick)
        {
            Tag = new MenuGlyph(glyph, glyphColor),
            ShortcutKeyDisplayString = shortcut
        };
    }

    private static void RefreshIcons(ToolStripItemCollection items)
    {
        var p = Theme.Current;
        foreach (ToolStripItem item in items)
        {
            if (item.Tag is MenuGlyph menuGlyph)
            {
                var previous = item.Image;
                item.Image = Glyphs.ToBitmap(menuGlyph.Glyph, item.Enabled ? menuGlyph.Color ?? p.Text : p.TextDisabled, 32);
                previous?.Dispose();
            }

            if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                RefreshIcons(parent.DropDownItems);
            }
        }
    }

    private sealed record MenuGlyph(char Glyph, Color? Color);

    private static void ApplyItemPadding(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is ToolStripMenuItem menuItem)
            {
                menuItem.Padding = new Padding(2, 5, 2, 5);
                if (menuItem.DropDown is ToolStripDropDownMenu dropDown && dropDown.Renderer is not ModernMenuRenderer)
                {
                    dropDown.Renderer = new ModernMenuRenderer();
                    dropDown.Padding = new Padding(4, 6, 4, 6);
                    dropDown.HandleCreated += (_, _) => NativeTheme.ApplyWindowFrame(dropDown.Handle, Theme.Current, WindowCorners.RoundSmall);
                }

                if (menuItem.HasDropDownItems)
                {
                    ApplyItemPadding(menuItem.DropDownItems);
                }
            }
        }
    }
}

/// <summary>Renderer with palette colors, rounded hover highlight and subtle separators.</summary>
public sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
{
    public ModernMenuRenderer()
        : base(new ModernColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Theme.Current.Card);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (NativeTheme.IsWindows11)
        {
            return; // DWM draws a rounded themed border.
        }

        using var pen = new Pen(Theme.Current.BorderStrong);
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Same surface as the menu: no separate gutter color.
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled || e.Item is MenuHeaderItem)
        {
            return;
        }

        var g = e.Graphics;
        Draw.PrepareHighQuality(g);
        var scale = e.ToolStrip?.DeviceDpi / 96f ?? 1f;
        var bounds = new RectangleF(2 * scale, 1, e.Item.Width - (4 * scale), e.Item.Height - 2);
        Draw.FillRounded(g, Theme.Current.Hover, bounds, 4 * scale);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var p = Theme.Current;
        var isShortcut = e.Item is ToolStripMenuItem menuItem && !string.IsNullOrEmpty(menuItem.ShortcutKeyDisplayString) &&
                         string.Equals(e.Text, menuItem.ShortcutKeyDisplayString, StringComparison.Ordinal);

        if (e.Item is MenuHeaderItem header)
        {
            e.TextColor = header.Emphasized ? p.Text : p.TextSecondary;
            if (header.Emphasized)
            {
                e.TextFont = Typography.BodyStrong;
            }
        }
        else if (!e.Item.Enabled)
        {
            e.TextColor = p.TextDisabled;
        }
        else
        {
            e.TextColor = isShortcut ? p.TextTertiary : p.Text;
        }

        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item is MenuHeaderItem { DotColor: { } dot })
        {
            var g = e.Graphics;
            Draw.PrepareHighQuality(g);
            var size = Math.Min(e.ImageRectangle.Width, e.ImageRectangle.Height) * 0.55f;
            var x = e.ImageRectangle.X + ((e.ImageRectangle.Width - size) / 2f);
            var y = e.ImageRectangle.Y + ((e.ImageRectangle.Height - size) / 2f);
            Draw.FillCircle(g, dot, new RectangleF(x, y, size, size));
            return;
        }

        base.OnRenderItemImage(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Current.Divider);
        var inset = (int)(8 * (e.ToolStrip?.DeviceDpi / 96f ?? 1f));
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? Theme.Current.TextDisabled : Theme.Current.TextSecondary;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        Draw.PrepareHighQuality(g);
        var rect = RectangleF.Inflate(e.ImageRectangle, 1, 1);
        Draw.FillRounded(g, p.AccentSoft, rect, 4);
        var w = rect.Width;
        var h = rect.Height;
        using var pen = new Pen(p.AccentText, Math.Max(1.5f, w / 9f))
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        g.DrawLines(pen,
        [
            new PointF(rect.X + (w * 0.27f), rect.Y + (h * 0.52f)),
            new PointF(rect.X + (w * 0.44f), rect.Y + (h * 0.68f)),
            new PointF(rect.X + (w * 0.74f), rect.Y + (h * 0.34f))
        ]);
    }

    private sealed class ModernColorTable : ProfessionalColorTable
    {
        public ModernColorTable()
        {
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground => Theme.Current.Card;

        public override Color MenuBorder => Theme.Current.BorderStrong;

        public override Color MenuItemBorder => Color.Transparent;

        public override Color MenuItemSelected => Theme.Current.Hover;

        public override Color ImageMarginGradientBegin => Theme.Current.Card;

        public override Color ImageMarginGradientMiddle => Theme.Current.Card;

        public override Color ImageMarginGradientEnd => Theme.Current.Card;

        public override Color SeparatorDark => Theme.Current.Divider;

        public override Color SeparatorLight => Theme.Current.Card;

        public override Color CheckBackground => Theme.Current.AccentSoft;

        public override Color CheckSelectedBackground => Theme.Current.AccentSoft;

        public override Color CheckPressedBackground => Theme.Current.AccentSoft;
    }
}

/// <summary>Owner-drawn tooltips that follow the palette (native tooltips stay light in dark mode).</summary>
public static class ModernToolTip
{
    public static ToolTip Create()
    {
        var tip = new ToolTip
        {
            OwnerDraw = true,
            InitialDelay = 450,
            ReshowDelay = 100,
            AutoPopDelay = 10000,
            UseAnimation = false,
            UseFading = false
        };
        tip.Popup += OnPopup;
        tip.Draw += OnDraw;
        return tip;
    }

    private static void OnPopup(object? sender, PopupEventArgs e)
    {
        var text = (sender as ToolTip)?.GetToolTip(e.AssociatedControl) ?? string.Empty;
        var scale = (e.AssociatedControl?.DeviceDpi ?? 96) / 96f;
        var maxWidth = (int)(320 * scale);
        var size = TextRenderer.MeasureText(text, Typography.Caption, new Size(maxWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        e.ToolTipSize = new Size(size.Width + (int)(18 * scale), size.Height + (int)(12 * scale));
    }

    private static void OnDraw(object? sender, DrawToolTipEventArgs e)
    {
        var p = Theme.Current;
        using (var background = new SolidBrush(p.IsDark ? p.Hover : p.Card))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }

        using (var border = new Pen(p.BorderStrong))
        {
            e.Graphics.DrawRectangle(border, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }

        var bounds = Rectangle.Inflate(e.Bounds, -8, -5);
        TextRenderer.DrawText(e.Graphics, e.ToolTipText, Typography.Caption, bounds, p.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
    }
}
