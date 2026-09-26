using System.Collections.Concurrent;
using ScreenVault.Core.Infrastructure;
using Serilog;

namespace ScreenVault.Core.Audio;

public sealed class AudioChunk
{
    public float[] Buffer { get; }
    public int Frames { get; }

    public AudioChunk(float[] buffer, int frames)
    {
        Buffer = buffer;
        Frames = frames;
    }
}

public sealed class StdinWriter : IDisposable
{
    private readonly BlockingCollection<AudioChunk> _queue;
    private readonly Thread _workerThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _byteConversionBuffer = new byte[9600 * 2 * sizeof(float)]; // MaxChunk = 9600 frames
    private readonly object _streamLock = new();

    private Stream? _currentStdin;
    private long _totalFramesWritten;
    private long _droppedChunks;

    public long TotalFramesWritten => Interlocked.Read(ref _totalFramesWritten);
    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);

    public StdinWriter(int queueCapacity = 100)
    {
        _queue = new BlockingCollection<AudioChunk>(queueCapacity);
        _workerThread = new Thread(ProcessQueue)
        {
            Name = "StdinWriterThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _workerThread.Start();
    }

    public void AttachStream(Stream stdinStream)
    {
        lock (_streamLock)
        {
            _currentStdin = stdinStream;
            Interlocked.Exchange(ref _totalFramesWritten, 0);
            Log.Information("StdinWriter: Attached new audio input stream.");
        }
    }

    public void DetachStream()
    {
        lock (_streamLock)
        {
            _currentStdin = null;
            Log.Information("StdinWriter: Detached audio input stream.");
        }
    }

    public void EnqueueChunk(float[] buffer, int frames)
    {
        if (_queue.IsAddingCompleted || _cts.IsCancellationRequested)
        {
            BufferPool.Floats.Return(buffer);
            return;
        }

        var chunk = new AudioChunk(buffer, frames);
        // Try to add; if queue full, drop oldest to avoid runaway memory
        if (!_queue.TryAdd(chunk))
        {
            if (_queue.TryTake(out var dropped))
            {
                BufferPool.Floats.Return(dropped.Buffer);
                Interlocked.Increment(ref _droppedChunks);
            }
            _queue.TryAdd(chunk);
        }
    }

    private void ProcessQueue()
    {
        try
        {
            foreach (var chunk in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    Stream? stream;
                    lock (_streamLock)
                    {
                        stream = _currentStdin;
                    }

                    if (stream != null)
                    {
                        var totalFloats = chunk.Frames * 2;
                        var byteCount = totalFloats * sizeof(float);

                        // Convert floats to bytes directly
                        Buffer.BlockCopy(chunk.Buffer, 0, _byteConversionBuffer, 0, byteCount);

                        stream.Write(_byteConversionBuffer, 0, byteCount);
                        stream.Flush();
                        Interlocked.Add(ref _totalFramesWritten, chunk.Frames);
                    }
                }
                catch (IOException ex)
                {
                    Log.Debug(ex, "StdinWriter encountered IOException (FFmpeg stdin closed).");
                    lock (_streamLock)
                    {
                        _currentStdin = null;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Unexpected error in StdinWriter.");
                }
                finally
                {
                    BufferPool.Floats.Return(chunk.Buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean exit
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        _workerThread.Join(1000);

        while (_queue.TryTake(out var leftover))
        {
            BufferPool.Floats.Return(leftover.Buffer);
        }

        _queue.Dispose();
        _cts.Dispose();
    }
}
