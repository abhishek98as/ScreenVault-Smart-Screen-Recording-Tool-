namespace ScreenVault.App.UI;

public sealed class MarkerNoteForm : Form
{
    private readonly TextBox _textBox;
    private readonly Button _btnSave;
    private readonly Button _btnCancel;

    public string NoteText => _textBox.Text.Trim();

    public MarkerNoteForm(string timestampText = "")
    {
        Text = "Add Marker — ScreenVault";
        var appIcon = Platform.AppIcon.Get();
        if (appIcon != null) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(360, 130);
        TopMost = true;

        var lblPrompt = new Label
        {
            Text = string.IsNullOrEmpty(timestampText)
                ? "Enter marker note (optional):"
                : $"Enter marker note for {timestampText}:",
            Location = new Point(16, 14),
            AutoSize = true
        };

        _textBox = new TextBox
        {
            Location = new Point(16, 40),
            Width = 328,
            Font = new Font("Segoe UI", 9.5f)
        };

        _btnSave = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(180, 80),
            Width = 75,
            Height = 28
        };

        _btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(265, 80),
            Width = 75,
            Height = 28
        };

        AcceptButton = _btnSave;
        CancelButton = _btnCancel;

        Controls.Add(lblPrompt);
        Controls.Add(_textBox);
        Controls.Add(_btnSave);
        Controls.Add(_btnCancel);

        Shown += (_, _) =>
        {
            _textBox.Focus();
        };
    }
}
