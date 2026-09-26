using System.Diagnostics;
using ScreenVault.Core.Infrastructure;
using Serilog;

namespace ScreenVault.Core.Audio;

public sealed class AudioPump : IDisposable
{
    private const int Rate = 48000;
    private const int MinChunk = 480;  // 10 ms
    private const int MaxChunk = 9600; // 200 ms

    private readonly StdinWriter _stdinWriter;
    private readonly Thread _pumpThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly float[] _scratchBuffer = new float[MaxChunk * 2];

    private volatile CaptureSource[] _sources = [];
    private long _framesWritten;
    private long _startTimestamp;
    private volatile bool _isPumpingActive;

    public long TotalFramesPushed => Interlocked.Read(ref _framesWritten);

    public AudioPump(StdinWriter stdinWriter)
    {
        _stdinWriter = stdinWriter ?? throw new ArgumentNullException(nameof(stdinWriter));
        _pumpThread = new Thread(PumpLoop)
        {
            Name = "AudioPumpThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _pumpThread.Start();
    }

    public void UpdateSources(CaptureSource[] newSources)
    {
        _sources = newSources ?? [];
    }

    public void ResetTimelineAndStart()
    {
        Interlocked.Exchange(ref _framesWritten, 0);
        Interlocked.Exchange(ref _startTimestamp, Stopwatch.GetTimestamp());
        _isPumpingActive = true;
        Log.Information("AudioPump: Timeline reset and pumping enabled.");
    }

    public void StopPumping()
    {
        _isPumpingActive = false;
        Log.Information("AudioPump: Pumping paused.");
    }

    private void PumpLoop()
    {
        Log.Debug("AudioPumpThread started.");
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!_isPumpingActive)
                {
                    Thread.Sleep(5);
                    continue;
                }

                var start = Interlocked.Read(ref _startTimestamp);
                var elapsedSec = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                var due = (long)(elapsedSec * Rate) - Interlocked.Read(ref _framesWritten);

                if (due < MinChunk)
                {
                    Thread.Sleep(2);
                    continue;
                }

                var n = (int)Math.Min(due, MaxChunk);
                var outBuf = BufferPool.Floats.Rent(n * 2);
                outBuf.AsSpan(0, n * 2).Clear();

                var currentSources = _sources;
                foreach (var src in currentSources)
                {
                    src.Jitter.Read(_scratchBuffer, n);
                    Mixer.Accumulate(outBuf.AsSpan(0, n * 2), _scratchBuffer.AsSpan(0, n * 2), n, src.GroupGainLinear);
                }

                SoftLimiter.Process(outBuf.AsSpan(0, n * 2), n);
                _stdinWriter.EnqueueChunk(outBuf, n);
                Interlocked.Add(ref _framesWritten, n);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean exit
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error in AudioPumpThread loop.");
        }
        Log.Debug("AudioPumpThread stopped.");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pumpThread.Join(1000);
        _cts.Dispose();
    }
}
