using System.Runtime.InteropServices;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.Platform;

public interface IHotkeyService : IDisposable
{
    /// <summary>Registers all shortcuts; returns the ones Windows refused (usually taken by another app).</summary>
    IReadOnlyList<string> RegisterHotkeys();
    void UnregisterHotkeys();
    event EventHandler? StartStopPressed;
    event EventHandler? MuteMicPressed;
    event EventHandler? AddMarkerPressed;
    event EventHandler? PauseResumePressed;
    event EventHandler? ShowStatusPressed;
}

public sealed class HotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNorepeat = 0x4000;

    private const int IdStartStop = 1000;
    private const int IdAddMarker = 1001;
    private const int IdPauseResume = 1002;
    private const int IdShowStatus = 1003;
    private const int IdMuteMic = 1004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly ISettingsService _settingsService;
    private readonly HotkeyWindow _window;
    private bool _isDisposed;

    public event EventHandler? StartStopPressed;
    public event EventHandler? MuteMicPressed;
    public event EventHandler? AddMarkerPressed;
    public event EventHandler? PauseResumePressed;
    public event EventHandler? ShowStatusPressed;

    public HotkeyService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _window = new HotkeyWindow(OnWmHotkey);
    }

    public IReadOnlyList<string> RegisterHotkeys()
    {
        UnregisterHotkeys();

        var hotkeys = _settingsService.Current.Hotkeys;
        var failed = new List<string>();

        // Default: Ctrl+Alt+Shift+R -> Keys.R
        RegisterParsedHotkey(IdStartStop, hotkeys.StartStop, Keys.R, failed);
        // Default: Ctrl+Alt+Shift+X -> Keys.X
        RegisterParsedHotkey(IdMuteMic, hotkeys.MuteMic, Keys.X, failed);
        // Default: Ctrl+Alt+Shift+M -> Keys.M (0x4D)
        RegisterParsedHotkey(IdAddMarker, hotkeys.AddMarker, Keys.M, failed);
        // Default: Ctrl+Alt+Shift+P -> Keys.P (0x50)
        RegisterParsedHotkey(IdPauseResume, hotkeys.PauseResume, Keys.P, failed);
        // Default: Ctrl+Alt+Shift+S -> Keys.S (0x53)
        RegisterParsedHotkey(IdShowStatus, hotkeys.ShowStatus, Keys.S, failed);
        return failed;
    }

    private void RegisterParsedHotkey(int id, string hotkeyString, Keys defaultKey, List<string> failed)
    {
        var (modifiers, key) = ParseHotkey(hotkeyString, defaultKey);
        var success = RegisterHotKey(_window.Handle, id, modifiers | ModNorepeat, (uint)key);
        if (success)
        {
            Log.Information("Registered global hotkey {KeyStr} (id: {Id})", hotkeyString, id);
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            Log.Warning("Failed to register global hotkey {KeyStr} (id: {Id}, Win32Error: {Err})", hotkeyString, id, err);
            failed.Add(string.IsNullOrWhiteSpace(hotkeyString) ? $"Ctrl+Alt+Shift+{defaultKey}" : hotkeyString);
        }
    }

    private static (uint Modifiers, Keys Key) ParseHotkey(string str, Keys defaultKey)
    {
        uint mod = 0;
        var key = defaultKey;

        if (string.IsNullOrWhiteSpace(str))
        {
            return (ModControl | ModAlt | ModShift, defaultKey);
        }

        var parts = str.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (string.Equals(p, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(p, "Control", StringComparison.OrdinalIgnoreCase))
            {
                mod |= ModControl;
            }
            else if (string.Equals(p, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                mod |= ModAlt;
            }
            else if (string.Equals(p, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                mod |= ModShift;
            }
            else if (p.Length == 1 && char.IsAsciiDigit(p[0]))
            {
                // "1" would otherwise parse as the numeric value 1 (the left mouse button).
                key = Keys.D0 + (p[0] - '0');
            }
            else if (!char.IsAsciiDigit(p[0]) && Enum.TryParse<Keys>(p, ignoreCase: true, out var parsedKey) && parsedKey > Keys.XButton2)
            {
                key = parsedKey;
            }
        }

        return (mod, key);
    }

    private void OnWmHotkey(int id)
    {
        switch (id)
        {
            case IdStartStop:
                StartStopPressed?.Invoke(this, EventArgs.Empty);
                break;
            case IdMuteMic:
                MuteMicPressed?.Invoke(this, EventArgs.Empty);
                break;
            case IdAddMarker:
                AddMarkerPressed?.Invoke(this, EventArgs.Empty);
                break;
            case IdPauseResume:
                PauseResumePressed?.Invoke(this, EventArgs.Empty);
                break;
            case IdShowStatus:
                ShowStatusPressed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void UnregisterHotkeys()
    {
        if (_window.Handle != IntPtr.Zero)
        {
            UnregisterHotKey(_window.Handle, IdStartStop);
            UnregisterHotKey(_window.Handle, IdMuteMic);
            UnregisterHotKey(_window.Handle, IdAddMarker);
            UnregisterHotKey(_window.Handle, IdPauseResume);
            UnregisterHotKey(_window.Handle, IdShowStatus);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        UnregisterHotkeys();
        _window.DestroyHandle();
    }

    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action<int> _onHotkey;

        public HotkeyWindow(Action<int> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                _onHotkey((int)m.WParam);
            }
            base.WndProc(ref m);
        }
    }
}
