using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI;

/// <summary>Small always-on-top popup for naming a marker (Enter saves, Esc cancels).</summary>
public sealed class MarkerNoteForm : ModernForm
{
    private readonly TextField _textBox;

    public string NoteText => _textBox.Text.Trim();

    public MarkerNoteForm(string timestampText = "")
        : base(WindowChrome.Borderless)
    {
        Text = "Add Marker — ScreenVault";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 172);
        TopMost = true;

        var badge = new GlyphBadge { Glyph = Glyphs.Flag, Tone = Tone.Accent, Bounds = new Rectangle(20, 20, 36, 36) };
        var title = new TextLabel("Add marker", Typography.Subtitle) { Location = new Point(68, 18) };
        var subtitle = new TextLabel(
            string.IsNullOrEmpty(timestampText) ? "Find this moment later in the recording" : $"At {timestampText} · find this moment later in the recording",
            Typography.Caption,
            TextTone.Secondary)
        {
            Location = new Point(68, 42)
        };

        _textBox = new TextField
        {
            Bounds = new Rectangle(20, 72, 380, 32),
            PlaceholderText = "What happened? (optional)",
            LeadingGlyph = Glyphs.Tag
        };

        var btnSave = new ModernButton("Save marker", ButtonKind.Primary, Glyphs.CheckMark)
        {
            DialogResult = DialogResult.OK,
            Bounds = new Rectangle(196, 122, 116, 32)
        };

        var btnCancel = new ModernButton("Cancel", ButtonKind.Secondary)
        {
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(320, 122, 80, 32)
        };

        AcceptButton = btnSave;
        CancelButton = btnCancel;

        Controls.AddRange([badge, title, subtitle, _textBox, btnSave, btnCancel]);
        EnableDrag(this);
        EnableDrag(title);
        EnableDrag(subtitle);

        ResumeLayout(false);
        PerformLayout();

        Shown += (_, _) =>
        {
            Activate();
            _textBox.Focus();
        };
    }
}
