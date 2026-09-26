using System.Collections.Concurrent;
using Serilog;

namespace ScreenVault.Core.Audio;

public sealed class AudioControlThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;

    public DateTime LastHeartbeatUtc => new(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc);

    public AudioControlThread()
    {
        _thread = new Thread(RunLoop)
        {
            Name = "AudioControlThread",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_cts.IsCancellationRequested) return;

        try
        {
            _queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // Queue already completed
        }
    }

    public void Send(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_cts.IsCancellationRequested) return;

        if (Thread.CurrentThread == _thread)
        {
            action();
            return;
        }

        using var doneEvent = new ManualResetEventSlim(false);
        Exception? error = null;

        Post(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                doneEvent.Set();
            }
        });

        doneEvent.Wait();
        if (error != null)
        {
            throw new InvalidOperationException("AudioControlThread action failed", error);
        }
    }

    private void RunLoop()
    {
        Log.Debug("AudioControlThread started.");
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_queue.TryTake(out var action, 1000, _cts.Token))
                {
                    Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Unhandled exception on AudioControlThread.");
                    }
                }
                else
                {
                    Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean exit
        }
        Log.Debug("AudioControlThread stopped.");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        _thread.Join(1000);
        _queue.Dispose();
        _cts.Dispose();
    }
}
