using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Settings;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private readonly HotkeyField _txtHkStartStop = new() { AccessibleName = "Start or stop shortcut" };
    private readonly HotkeyField _txtHkMuteMic = new() { AccessibleName = "Mute microphone shortcut" };
    private readonly HotkeyField _txtHkMarker = new() { AccessibleName = "Add marker shortcut" };
    private readonly HotkeyField _txtHkPause = new() { AccessibleName = "Pause or resume shortcut" };
    private readonly HotkeyField _txtHkStatus = new() { AccessibleName = "Show status shortcut" };

    private StackPanel BuildShortcutsPage()
    {
        foreach (var hotkey in new[] { _txtHkStartStop, _txtHkMuteMic, _txtHkMarker, _txtHkPause, _txtHkStatus })
        {
            hotkey.Width = 260;
        }

        var hotkeys = CreatePage("Shortcuts", "Global keyboard shortcuts work in any app. Click a shortcut and press a new key combination.");
        hotkeys.Controls.Add(Card(
            Row("Start / stop & save", null, _txtHkStartStop, Glyphs.Record),
            Row("Mute microphone in recording", null, _txtHkMuteMic, Glyphs.Microphone),
            Row("Add marker", null, _txtHkMarker, Glyphs.Flag),
            Row("Pause / resume", null, _txtHkPause, Glyphs.Pause),
            Row("Show status window", null, _txtHkStatus, Glyphs.Monitor)));

        var btnResetHotkeys = new ModernButton("Restore defaults", ButtonKind.Secondary, Glyphs.Refresh) { Size = new Size(150, 32) };
        btnResetHotkeys.Click += (_, _) =>
        {
            var defaults = new HotkeySettings();
            _txtHkStartStop.Hotkey = defaults.StartStop;
            _txtHkMuteMic.Hotkey = defaults.MuteMic;
            _txtHkMarker.Hotkey = defaults.AddMarker;
            _txtHkPause.Hotkey = defaults.PauseResume;
            _txtHkStatus.Hotkey = defaults.ShowStatus;
        };
        hotkeys.Controls.Add(Card(Row("Default shortcuts", "Ctrl + Alt + Shift with R, X, M, P and S.", btnResetHotkeys, Glyphs.Keyboard)));

        return hotkeys;
    }

    private void LoadShortcutsSettings(AppSettings s)
    {
        _txtHkStartStop.Hotkey = s.Hotkeys.StartStop;
        _txtHkMuteMic.Hotkey = s.Hotkeys.MuteMic;
        _txtHkMarker.Hotkey = s.Hotkeys.AddMarker;
        _txtHkPause.Hotkey = s.Hotkeys.PauseResume;
        _txtHkStatus.Hotkey = s.Hotkeys.ShowStatus;
    }

    private void SaveShortcutsSettings(AppSettings s)
    {
        s.Hotkeys.StartStop = _txtHkStartStop.Hotkey;
        s.Hotkeys.MuteMic = _txtHkMuteMic.Hotkey;
        s.Hotkeys.AddMarker = _txtHkMarker.Hotkey;
        s.Hotkeys.PauseResume = _txtHkPause.Hotkey;
        s.Hotkeys.ShowStatus = _txtHkStatus.Hotkey;
    }
}
