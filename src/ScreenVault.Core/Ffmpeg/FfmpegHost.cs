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
    // How long a stop waits for the output reader to drain what FFmpeg wrote before exiting.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    private readonly JobObject _jobObject = new();
    private readonly object _lock = new();
    private readonly Queue<string> _lastStderrLines = new(50);
    private readonly FfmpegProgressParser _progressParser = new();

    private Process? _process;
    private Task? _stdoutDone;
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
                return _process != null && !HasExited(_process);
            }
        }
    }

    public Stream AudioInput
    {
        get
        {
            lock (_lock)
            {
                if (_process == null || HasExited(_process))
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
                if (_process != null && !HasExited(_process))
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

            // Release the previous (exited) process so handles don't pile up across restarts.
            _drainCts?.Dispose();
            _drainCts = null;
            _process?.Dispose();
            _process = null;

            _isExplicitStop = false;
            Interlocked.Exchange(ref _stdoutBytesTotal, 0);
            _lastStdoutActivityUtc = DateTime.UtcNow;
            _lastStderrLines.Clear();
            _progressParser.Reset();
            _lastProgress = new FfmpegProgress();

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
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException($"Failed to launch FFmpeg process from {spec.ExecutablePath}.");
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }

            _process = process;
            _jobObject.AssignProcess(process);
            var drainCts = new CancellationTokenSource();
            _drainCts = drainCts;

            process.Exited += (_, _) => OnProcessExited(process);

            // Dedicated stdout reader thread (the recording itself flows through here).
            var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stdoutDone = stdoutDone.Task;
            var stdoutThread = new Thread(() => ReadStdout(process, sink, stdoutDone, drainCts.Token))
            {
                Name = "FfmpegStdoutReader",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            stdoutThread.Start();

            // Stderr carries progress and diagnostics.
            _stderrTask = Task.Run(() => ReadStderrAsync(process, drainCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private void ReadStdout(Process process, ISegmentSink sink, TaskCompletionSource done, CancellationToken ct)
    {
        const int bufferSize = 64 * 1024;
        var buffer = BufferPool.Bytes.Rent(bufferSize);

        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    Log.Debug(ex, "FFmpeg stdout stream closed.");
                    break;
                }

                if (bytesRead <= 0)
                {
                    break;
                }

                Interlocked.Add(ref _stdoutBytesTotal, bytesRead);
                lock (_lock)
                {
                    _lastStdoutActivityUtc = DateTime.UtcNow;
                }

                try
                {
                    sink.Write(buffer.AsSpan(0, bytesRead));
                }
                catch (Exception ex)
                {
                    // The recording can't be written anywhere (e.g. every storage location is full or
                    // unreachable). Stop FFmpeg so the controller notices at once and retries with backoff,
                    // instead of FFmpeg blocking on a pipe nobody reads.
                    Log.Error(ex, "Could not write recording data; stopping FFmpeg so the recorder can recover.");
                    TryKill(process);
                    break;
                }
            }
        }
        finally
        {
            BufferPool.Bytes.Return(buffer);
            done.TrySetResult();
        }
    }

    /// <summary>"frame=123", "out_time_ms=…", "progress=continue": one key=value per line.</summary>
    private static bool IsProgressLine(string line)
    {
        var equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return false;
        }

        for (var i = 0; i < equals; i++)
        {
            var c = line[i];
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private async Task ReadStderrAsync(Process process, CancellationToken ct)
    {
        var messagesLogged = 0;
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

                lock (_lock)
                {
                    if (!ReferenceEquals(process, _process))
                    {
                        break;
                    }

                    _lastProgress = _progressParser.ParseLine(line, out _);
                    if (_lastStderrLines.Count >= 50)
                    {
                        _lastStderrLines.Dequeue();
                    }
                    _lastStderrLines.Enqueue(line);
                }

                // Everything that isn't a "-progress" key=value line is a warning or an error (FFmpeg
                // runs with -loglevel level+warning), e.g. about the audio input: keep it in the log.
                if (line.Length > 0 && !IsProgressLine(line))
                {
                    messagesLogged++;
                    if (messagesLogged <= 50 || messagesLogged % 500 == 0)
                    {
                        Log.Warning("[FFmpeg] {Line} (message {Count})", line, messagesLogged);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "FFmpeg stderr stream read terminated.");
        }
        catch (OperationCanceledException)
        {
            // Stop requested.
        }
    }

    private void OnProcessExited(Process process)
    {
        int exitCode;
        List<string> stderrSnapshot;
        bool isExplicitStop;

        lock (_lock)
        {
            // Exited events are raised on the thread pool and can arrive after a newer process
            // has been started; only report the process we are currently running.
            if (!ReferenceEquals(process, _process)) return;
            exitCode = HasExited(process) ? SafeExitCode(process) : -1;
            stderrSnapshot = _lastStderrLines.ToList();
            isExplicitStop = _isExplicitStop;
        }

        Log.Information("FFmpeg process exited with code {ExitCode}. ExplicitStop: {Explicit}",
            exitCode, isExplicitStop);

        if (!isExplicitStop)
        {
            if (stderrSnapshot.Count > 0)
            {
                Log.Warning("Last FFmpeg output before the unexpected exit: {Lines}", string.Join(" | ", stderrSnapshot.TakeLast(5)));
            }

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
        Task? stdoutDone;
        Task? stderrTask;
        CancellationTokenSource? drainCts;
        lock (_lock)
        {
            _isExplicitStop = true;
            procToStop = _process;
            stdoutDone = _stdoutDone;
            stderrTask = _stderrTask;
            drainCts = _drainCts;
        }

        if (procToStop == null)
        {
            return;
        }

        if (!HasExited(procToStop))
        {
            Log.Information("Gracefully stopping FFmpeg process...");
            try
            {
                // Close stdin so FFmpeg sees audio EOF and finishes muxing
                procToStop.StandardInput.Close();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error closing FFmpeg stdin.");
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(gracefulTimeout);
                await procToStop.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                Log.Information("FFmpeg exited cleanly.");
            }
            catch (OperationCanceledException)
            {
                Log.Warning("FFmpeg did not exit within graceful timeout ({Timeout}s). Killing process.", gracefulTimeout.TotalSeconds);
                Kill();
            }
        }

        // FFmpeg's last packets (the end of the recording) may still be in the pipe: let the reader
        // drain them before the caller closes the segment. Only cancel the readers if they hang.
        await WaitQuietlyAsync(stdoutDone, DrainTimeout, "stdout").ConfigureAwait(false);
        await WaitQuietlyAsync(stderrTask, TimeSpan.FromSeconds(1), "stderr").ConfigureAwait(false);

        try
        {
            drainCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A newer start already replaced and disposed it.
        }
    }

    public void Kill()
    {
        lock (_lock)
        {
            _isExplicitStop = true;
            if (_process != null && !HasExited(_process))
            {
                Log.Warning("Killing FFmpeg process {Pid}...", SafeId(_process));
                TryKill(_process);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        _jobObject.Dispose();
        lock (_lock)
        {
            _drainCts?.Dispose();
            _drainCts = null;
            _process?.Dispose();
            _process = null;
        }
    }

    private static async Task WaitQuietlyAsync(Task? task, TimeSpan timeout, string name)
    {
        if (task == null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warning("FFmpeg {Stream} reader did not finish within {Timeout}s after the process stopped.", name, timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FFmpeg {Stream} reader ended with an error.", name);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while killing FFmpeg process.");
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static int SafeId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
