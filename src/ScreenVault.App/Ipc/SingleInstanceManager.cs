using ScreenVault.Core.Infrastructure;

namespace ScreenVault.App.Ipc;

public sealed class SingleInstanceManager : IDisposable
{
    public const string MutexName = AppConstants.AppMutexName;
    private Mutex? _mutex;
    private bool _hasHandle;

    public bool IsPrimaryInstance => _hasHandle;

    public SingleInstanceManager()
    {
        TryAcquire();
    }

    public bool TryAcquire()
    {
        if (_mutex == null)
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out _hasHandle);
        }
        return _hasHandle;
    }

    public void Dispose()
    {
        if (_hasHandle && _mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore during shutdown
            }
            _hasHandle = false;
        }

        _mutex?.Dispose();
        _mutex = null;
    }
}
