using System.Runtime.InteropServices;
using ScreenVault.Core.Recording;
using Serilog;

namespace ScreenVault.App.Platform;

public sealed class RestartManagerWindow : NativeWindow, IDisposable
{
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;
    private const uint ENDSESSION_CLOSEAPP = 0x00000001;

    private readonly Func<bool> _isRecordingFunc;
    private readonly Func<Task> _stopRecordingFunc;
    private bool _isDisposed;

    public RestartManagerWindow(Func<bool> isRecordingFunc, Func<Task> stopRecordingFunc)
    {
        _isRecordingFunc = isRecordingFunc ?? throw new ArgumentNullException(nameof(isRecordingFunc));
        _stopRecordingFunc = stopRecordingFunc ?? throw new ArgumentNullException(nameof(stopRecordingFunc));

        CreateHandle(new CreateParams
        {
            Caption = "ScreenVault_RestartManager_Listener",
            Style = 0
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_QUERYENDSESSION)
        {
            var isCloseApp = ((long)m.LParam & ENDSESSION_CLOSEAPP) != 0;
            Log.Information("WM_QUERYENDSESSION received (isCloseApp={IsCloseApp})", isCloseApp);

            if (_isRecordingFunc())
            {
                ShutdownBlocker.BlockShutdown(Handle, "ScreenVault is saving the current recording...");
            }

            m.Result = (IntPtr)1; // Agree to query
            return;
        }
        else if (m.Msg == WM_ENDSESSION)
        {
            var isEnding = m.WParam != IntPtr.Zero;
            var isCloseApp = ((long)m.LParam & ENDSESSION_CLOSEAPP) != 0;
            Log.Information("WM_ENDSESSION received (isEnding={IsEnding}, isCloseApp={IsCloseApp})", isEnding, isCloseApp);

            if (isEnding)
            {
                try
                {
                    if (_isRecordingFunc())
                    {
                        Log.Information("Restart Manager / system shutdown requested; saving resume.json and stopping recording...");
                        ResumeStateService.SaveResumeState(true);
                    }
                    _stopRecordingFunc().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error while stopping recording during WM_ENDSESSION");
                }
                finally
                {
                    ShutdownBlocker.UnblockShutdown(Handle);
                }
            }

            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (Handle != IntPtr.Zero)
        {
            ShutdownBlocker.UnblockShutdown(Handle);
            DestroyHandle();
        }
    }
}
