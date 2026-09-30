using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI.Controls;

/// <summary>
/// Editor for a list of string items (e.g. process names) with an add field, Add button, and Remove button.
/// </summary>
public sealed class StringListEditor : Control, IThemeAware
{
    private readonly ThemedListBox _listBox;
    private readonly TextField _txtInput;
    private readonly ModernButton _btnAdd;
    private readonly ModernButton _btnRemove;

    public event EventHandler? Changed;

    public StringListEditor(string placeholder = "Add item...")
    {
        _listBox = new ThemedListBox
        {
            ItemHeightLogical = 30,
            Dock = DockStyle.Fill
        };
        _listBox.SelectedIndexChanged += (_, _) => UpdateButtonStates();

        _txtInput = new TextField
        {
            PlaceholderText = placeholder,
            Dock = DockStyle.Fill
        };
        _txtInput.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                AddItem();
            }
        };

        _btnAdd = new ModernButton("Add", ButtonKind.Secondary, Glyphs.Add)
        {
            AutoSize = true,
            Dock = DockStyle.Right
        };
        _btnAdd.Click += (_, _) => AddItem();

        _btnRemove = new ModernButton("Remove", ButtonKind.Subtle, Glyphs.Delete)
        {
            AutoSize = true,
            Dock = DockStyle.Right,
            Enabled = false
        };
        _btnRemove.Click += (_, _) => RemoveSelectedItem();

        var inputPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0, 4, 0, 0)
        };
        inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        inputPanel.Controls.Add(_txtInput, 0, 0);
        inputPanel.Controls.Add(_btnAdd, 1, 0);
        inputPanel.Controls.Add(_btnRemove, 2, 0);

        Controls.Add(_listBox);
        Controls.Add(inputPanel);

        Size = new Size(320, 160);
    }

    public List<string> GetItems()
    {
        var list = new List<string>(_listBox.Items.Count);
        foreach (var item in _listBox.Items)
        {
            if (item != null)
                list.Add(item.ToString()!);
        }
        return list;
    }

    public void SetItems(IEnumerable<string> items)
    {
        _listBox.Items.Clear();
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
                _listBox.Items.Add(item.Trim());
        }
        UpdateButtonStates();
    }

    private void AddItem()
    {
        var text = _txtInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Don't add duplicate
        foreach (var existing in _listBox.Items)
        {
            if (string.Equals(existing?.ToString(), text, StringComparison.OrdinalIgnoreCase))
                return;
        }

        _listBox.Items.Add(text);
        _txtInput.Text = string.Empty;
        _listBox.SelectedIndex = _listBox.Items.Count - 1;
        UpdateButtonStates();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveSelectedItem()
    {
        if (_listBox.SelectedIndex >= 0)
        {
            var idx = _listBox.SelectedIndex;
            _listBox.Items.RemoveAt(idx);
            if (_listBox.Items.Count > 0)
                _listBox.SelectedIndex = Math.Clamp(idx, 0, _listBox.Items.Count - 1);
            UpdateButtonStates();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateButtonStates()
    {
        _btnRemove.Enabled = _listBox.SelectedIndex >= 0;
    }

    public void ApplyTheme()
    {
        _listBox.ApplyTheme();
        _txtInput.ApplyTheme();
        _btnAdd.ApplyTheme();
        _btnRemove.ApplyTheme();
    }
}
