using System.Runtime.InteropServices;
using Serilog;

namespace ScreenVault.App.Platform;

/// <summary>
/// NativeWindow that registers for Windows display power notifications (GUID_CONSOLE_DISPLAY_STATE)
/// to detect screen turning on or off (useful for Modern Standby laptops).
/// </summary>
public sealed class DisplayStateWatcher : NativeWindow, IDisposable
{
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    private IntPtr _hNotify;
    private bool _isDisposed;

    public event Action? DisplayTurnedOff;
    public event Action? DisplayTurnedOn;

    public DisplayStateWatcher()
    {
        var cp = new CreateParams
        {
            Caption = "ScreenVault_DisplayStateWatcher"
        };
        CreateHandle(cp);

        var guid = GUID_CONSOLE_DISPLAY_STATE;
        _hNotify = RegisterPowerSettingNotification(Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_hNotify == IntPtr.Zero)
        {
            Log.Warning("Failed to register display power setting notification: {Error}", Marshal.GetLastWin32Error());
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE)
        {
            var pbs = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
            if (pbs.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
            {
                // 0 = off, 1 = on, 2 = dimmed
                var state = pbs.Data;
                Log.Information("Display power setting changed: {State}", state);
                if (state == 0)
                {
                    DisplayTurnedOff?.Invoke();
                }
                else if (state == 1)
                {
                    DisplayTurnedOn?.Invoke();
                }
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_hNotify != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_hNotify);
            _hNotify = IntPtr.Zero;
        }

        DestroyHandle();
    }
}
