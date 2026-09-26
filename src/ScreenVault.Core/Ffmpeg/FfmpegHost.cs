using System.Diagnostics;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.Output;
using Serilog;

namespace ScreenVault.Core.Ffmpeg;

public sealed class FfmpegExitedEventArgs : EventArgs
{
    public int ExitCode { get; init; }
    public IReadOnlyList<string> LastStderrLines { get; init; } = [];
}

public interface IFfmpegHost : IAsyncDisposable
{
    Task StartAsync(FfmpegLaunchSpec spec, ISegmentSink sink, CancellationToken ct);
    Task StopAsync(TimeSpan gracefulTimeout);
    void Kill();
    bool IsRunning { get; }
    Stream AudioInput { get; }
    long StdoutBytesTotal { get; }
    DateTime LastStdoutActivityUtc { get; }
    FfmpegProgress LastProgress { get; }
    long WorkingSet64 { get; }
    event EventHandler<FfmpegExitedEventArgs>? Exited;
}

public sealed class FfmpegHost : IFfmpegHost
{
    private readonly JobObject _jobObject = new();
    private readonly object _lock = new();
    private readonly Queue<string> _lastStderrLines = new(50);
    private readonly FfmpegProgressParser _progressParser = new();

    private Process? _process;
    private Thread? _stdoutThread;
    private Task? _stderrTask;
    private CancellationTokenSource? _drainCts;
    private long _stdoutBytesTotal;
    private DateTime _lastStdoutActivityUtc;
    private FfmpegProgress _lastProgress = new();
    private bool _isExplicitStop;

    public event EventHandler<FfmpegExitedEventArgs>? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public Stream AudioInput
    {
        get
        {
            lock (_lock)
            {
                if (_process == null || _process.HasExited)
                {
                    throw new InvalidOperationException("FFmpeg process is not running.");
                }
                return _process.StandardInput.BaseStream;
            }
        }
    }

    public long StdoutBytesTotal => Interlocked.Read(ref _stdoutBytesTotal);

    public DateTime LastStdoutActivityUtc
    {
        get
        {
            lock (_lock)
            {
                return _lastStdoutActivityUtc;
            }
        }
    }

    public FfmpegProgress LastProgress
    {
        get
        {
            lock (_lock)
            {
                return _lastProgress;
            }
        }
    }

    public long WorkingSet64
    {
        get
        {
            lock (_lock)
            {
                if (_process is { HasExited: false })
                {
                    try
                    {
                        _process.Refresh();
                        return _process.WorkingSet64;
                    }
                    catch
                    {
                        return 0;
                    }
                }
                return 0;
            }
        }
    }

    public Task StartAsync(FfmpegLaunchSpec spec, ISegmentSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(sink);

        lock (_lock)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("FFmpeg host is already running a process.");
            }

            _isExplicitStop = false;
            Interlocked.Exchange(ref _stdoutBytesTotal, 0);
            _lastStdoutActivityUtc = DateTime.UtcNow;
            _lastStderrLines.Clear();

            var startInfo = new ProcessStartInfo
            {
                FileName = spec.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var arg in spec.Arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            Log.Information("Launching FFmpeg process: {Path} {Args}",
                spec.ExecutablePath,
                string.Join(" ", spec.Arguments));

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to launch FFmpeg process from {spec.ExecutablePath}.");
            }

            _process = process;
            _jobObject.AssignProcess(process);
            _drainCts = new CancellationTokenSource();

            process.Exited += (_, _) => OnProcessExited();

            // Start dedicated stdout reader thread
            _stdoutThread = new Thread(() => ReadStdout(process, sink, _drainCts.Token))
            {
                Name = "FfmpegStdoutReader",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _stdoutThread.Start();

            // Start stderr reader task
            _stderrTask = Task.Run(() => ReadStderrAsync(process, _drainCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private void ReadStdout(Process process, ISegmentSink sink, CancellationToken ct)
    {
        const int bufferSize = 64 * 1024;
        var buffer = BufferPool.Bytes.Rent(bufferSize);

        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    break;
                }

                Interlocked.Add(ref _stdoutBytesTotal, bytesRead);
                lock (_lock)
                {
                    _lastStdoutActivityUtc = DateTime.UtcNow;
                }

                sink.Write(buffer.AsSpan(0, bytesRead));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "FFmpeg stdout stream read terminated.");
        }
        finally
        {
            BufferPool.Bytes.Return(buffer);
        }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            var reader = process.StandardError;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                var progress = _progressParser.ParseLine(line, out _);
                lock (_lock)
                {
                    _lastProgress = progress;
                    if (_lastStderrLines.Count >= 50)
                    {
                        _lastStderrLines.Dequeue();
                    }
                    _lastStderrLines.Enqueue(line);
                }

                // If not progress key=value, log it
                if (!line.Contains('='))
                {
                    Log.Debug("[FFmpeg stderr] {Line}", line);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "FFmpeg stderr stream read terminated.");
        }
    }

    private void OnProcessExited()
    {
        int exitCode;
        List<string> stderrSnapshot;

        lock (_lock)
        {
            if (_process == null) return;
            exitCode = _process.HasExited ? _process.ExitCode : -1;
            stderrSnapshot = _lastStderrLines.ToList();
        }

        Log.Information("FFmpeg process exited with code {ExitCode}. ExplicitStop: {Explicit}",
            exitCode, _isExplicitStop);

        if (!_isExplicitStop)
        {
            Exited?.Invoke(this, new FfmpegExitedEventArgs
            {
                ExitCode = exitCode,
                LastStderrLines = stderrSnapshot
            });
        }
    }

    public async Task StopAsync(TimeSpan gracefulTimeout)
    {
        Process? procToStop;
        lock (_lock)
        {
            _isExplicitStop = true;
            procToStop = _process;
        }

        if (procToStop == null || procToStop.HasExited)
        {
            return;
        }

        Log.Information("Gracefully stopping FFmpeg process...");
        try
        {
            // Close stdin so FFmpeg sees audio EOF and finishes muxing
            try
            {
                procToStop.StandardInput.Close();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error closing FFmpeg stdin.");
            }

            using var timeoutCts = new CancellationTokenSource(gracefulTimeout);
            await procToStop.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            Log.Information("FFmpeg exited cleanly.");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("FFmpeg did not exit within graceful timeout ({Timeout}s). Killing process.", gracefulTimeout.TotalSeconds);
            Kill();
        }
        finally
        {
            _drainCts?.Cancel();
        }
    }

    public void Kill()
    {
        lock (_lock)
        {
            _isExplicitStop = true;
            if (_process is { HasExited: false })
            {
                try
                {
                    Log.Warning("Killing FFmpeg process {Pid}...", _process.Id);
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error while killing FFmpeg process.");
                }
            }
            _drainCts?.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        _jobObject.Dispose();
        _drainCts?.Dispose();
        _process?.Dispose();
    }
}
