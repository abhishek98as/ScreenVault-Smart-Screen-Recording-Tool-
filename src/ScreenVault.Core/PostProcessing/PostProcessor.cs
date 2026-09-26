using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Threading.Channels;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Serilog;

namespace ScreenVault.Core.PostProcessing;

public interface IPostProcessor : IDisposable
{
    void Enqueue(RemuxJob job);
    int PendingJobCount { get; }
    event EventHandler<RemuxCompletedEventArgs>? JobCompleted;
    Task<MergeResult?> FinalizeSessionAsync(string sessionId, SavingSettings savingSettings, CancellationToken ct = default);
}

public sealed class PostProcessor : IPostProcessor
{
    private readonly IFfprobeClient _ffprobeClient;
    private readonly IChapterWriter _chapterWriter;
    private readonly ISessionStore _sessionStore;
    private readonly IFileSystem _fileSystem;
    private readonly string _ffmpegPath;

    private readonly Channel<RemuxJob> _channel = Channel.CreateUnbounded<RemuxJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private int _pendingCount;
    private bool _isDisposed;

    public int PendingJobCount => Volatile.Read(ref _pendingCount);
    public event EventHandler<RemuxCompletedEventArgs>? JobCompleted;

    public PostProcessor(
        IFfprobeClient ffprobeClient,
        IChapterWriter chapterWriter,
        ISessionStore sessionStore,
        IFileSystem? fileSystem = null,
        string? ffmpegPath = null)
    {
        _ffprobeClient = ffprobeClient ?? throw new ArgumentNullException(nameof(ffprobeClient));
        _chapterWriter = chapterWriter ?? throw new ArgumentNullException(nameof(chapterWriter));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _fileSystem = fileSystem ?? new FileSystem();

        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            var paths = new FfmpegLocator(_fileSystem).Locate();
            _ffmpegPath = paths.FfmpegPath;
        }
        else
        {
            _ffmpegPath = ffmpegPath;
        }

        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public void Enqueue(RemuxJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Interlocked.Increment(ref _pendingCount);
        _channel.Writer.TryWrite(job);
        Log.Information("Enqueued remux job for {TsPath} -> {FinalPath}", job.TsPath, job.FinalPath);
    }

    private async Task ProcessQueueAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            RemuxJob job;
            try
            {
                job = await _channel.Reader.ReadAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ExecuteJobAsync(job, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error processing remux job for {TsPath}", job.TsPath);
                NotifyCompleted(job, success: false, errorMessage: ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingCount);
            }
        }
    }

    private async Task ExecuteJobAsync(RemuxJob job, CancellationToken ct)
    {
        if (!_fileSystem.File.Exists(job.TsPath))
        {
            Log.Warning("Remux source file does not exist: {TsPath}", job.TsPath);
            UpdateManifestRemuxStatus(job, "Failed");
            NotifyCompleted(job, success: false, errorMessage: "Source file not found");
            return;
        }

        if (job.OutputFormat == OutputContainerFormat.Ts)
        {
            Log.Information("Format is TS; no remux needed for {TsPath}", job.TsPath);
            UpdateManifestRemuxStatus(job, "NotRequired");
            NotifyCompleted(job, success: true);
            return;
        }

        var locationRoot = StorageDirectoryHelper.GetLocationRoot(_fileSystem, job.FinalPath);
        var tempDir = StorageDirectoryHelper.GetTempDirectory(_fileSystem, locationRoot);
        var partialFileName = _fileSystem.Path.GetFileName(job.FinalPath) + ".partial";
        var partialPath = _fileSystem.Path.Combine(tempDir, partialFileName);
        string? chapterFile = null;

        try
        {
            var finalDir = _fileSystem.Path.GetDirectoryName(job.FinalPath);
            if (!string.IsNullOrEmpty(finalDir) && !_fileSystem.Directory.Exists(finalDir))
            {
                _fileSystem.Directory.CreateDirectory(finalDir);
            }

            // 1. Generate chapters if any markers apply
            if (job.Markers.Count > 0)
            {
                var chapterPath = _fileSystem.Path.Combine(tempDir, $"chapters_p{job.SegmentIndex:D3}_{Guid.NewGuid():N}.txt");
                chapterFile = _chapterWriter.WriteSegmentChapters(
                    job.SessionTitle ?? $"ScreenVault session {job.SessionId}",
                    job.SegmentStartUtc,
                    job.SegmentEndUtc,
                    job.Markers,
                    chapterPath);
            }

            // 2. Build FFmpeg command
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"-hide_banner -loglevel error -y -i \"{job.TsPath}\" ");
            if (!string.IsNullOrEmpty(chapterFile) && _fileSystem.File.Exists(chapterFile))
            {
                sb.Append(CultureInfo.InvariantCulture, $"-i \"{chapterFile}\" -map 0 -map_chapters 1 ");
            }
            else
            {
                sb.Append("-map 0 ");
            }

            sb.Append("-c copy -bsf:a aac_adtstoasc ");

            var title = !string.IsNullOrWhiteSpace(job.SessionTitle)
                ? job.SessionTitle
                : $"ScreenVault session {job.SessionId}";
            sb.Append(CultureInfo.InvariantCulture, $"-metadata title=\"{title}\" ");
            sb.Append(CultureInfo.InvariantCulture, $"-metadata comment=\"ScreenVault session {job.SessionId}\" ");
            sb.Append(CultureInfo.InvariantCulture, $"-metadata creation_time=\"{job.SegmentStartUtc:yyyy-MM-ddTHH:mm:ssZ}\" ");

            if (job.OutputFormat == OutputContainerFormat.Mp4)
            {
                sb.Append(CultureInfo.InvariantCulture, $"-movflags +faststart -f mp4 \"{partialPath}\"");
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"-f matroska \"{partialPath}\"");
            }

            // 3. Run FFmpeg at BelowNormal priority
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = sb.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                try
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch
                {
                    // Ignore if permission denied on setting priority
                }

                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"FFmpeg remux failed with code {process.ExitCode}: {stderr}");
                }
            }

            // 4. Probe & Verify with ffprobe
            var tsProbe = await _ffprobeClient.ProbeAsync(job.TsPath, ct).ConfigureAwait(false);
            var remuxProbe = await _ffprobeClient.ProbeAsync(partialPath, ct).ConfigureAwait(false);

            if (tsProbe == null || remuxProbe == null)
            {
                throw new InvalidOperationException("Failed to probe media files with ffprobe.");
            }

            if (!_ffprobeClient.ValidateRemux(tsProbe, remuxProbe, out var failureReason))
            {
                throw new InvalidOperationException($"Remux validation failed: {failureReason}");
            }

            // 5. Success! Move partial to final and delete TS if requested
            if (_fileSystem.File.Exists(job.FinalPath))
            {
                _fileSystem.File.Delete(job.FinalPath);
            }
            _fileSystem.File.Move(partialPath, job.FinalPath);

            if (!job.KeepTsAfterRemux && _fileSystem.File.Exists(job.TsPath))
            {
                try
                {
                    _fileSystem.File.Delete(job.TsPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not delete source TS file after successful remux: {TsPath}", job.TsPath);
                }
            }

            UpdateManifestRemuxStatus(job, "Done");
            Log.Information("Successfully remuxed {TsPath} to {FinalPath}", job.TsPath, job.FinalPath);
            NotifyCompleted(job, success: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remux {TsPath}. Keeping TS file.", job.TsPath);

            if (_fileSystem.File.Exists(partialPath))
            {
                try
                {
                    _fileSystem.File.Delete(partialPath);
                }
                catch
                {
                    // Ignore
                }
            }

            UpdateManifestRemuxStatus(job, "Failed");

            // Retry once after 10 minutes if RetryCount == 0
            if (job.RetryCount == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(10), _cts.Token).ConfigureAwait(false);
                        job.RetryCount++;
                        Enqueue(job);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down
                    }
                }, CancellationToken.None);
            }

            NotifyCompleted(job, success: false, errorMessage: ex.Message);
        }
        finally
        {
            if (!string.IsNullOrEmpty(chapterFile) && _fileSystem.File.Exists(chapterFile))
            {
                try
                {
                    _fileSystem.File.Delete(chapterFile);
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }

    private void UpdateManifestRemuxStatus(RemuxJob job, string status)
    {
        if (string.IsNullOrEmpty(job.SessionId))
        {
            return;
        }

        try
        {
            var manifest = _sessionStore.Load(job.SessionId);
            if (manifest != null)
            {
                var segment = manifest.Segments.FirstOrDefault(s => s.Index == job.SegmentIndex);
                if (segment != null)
                {
                    segment.Remux = status;
                    _sessionStore.Save(manifest);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update manifest status for session {SessionId}", job.SessionId);
        }
    }

    private void NotifyCompleted(RemuxJob job, bool success, string? errorMessage = null)
    {
        JobCompleted?.Invoke(this, new RemuxCompletedEventArgs(job, success, errorMessage));
    }

    public async Task<MergeResult?> FinalizeSessionAsync(string sessionId, SavingSettings savingSettings, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(savingSettings);

        // Wait for all remux jobs of this session to finish (up to 60s)
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60) && !ct.IsCancellationRequested)
        {
            var manifest = _sessionStore.Load(sessionId);
            if (manifest == null) break;
            var anyPending = manifest.Segments.Any(s => string.Equals(s.Remux, "Pending", StringComparison.OrdinalIgnoreCase));
            if (!anyPending) break;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        if (savingSettings.MergeOnSave)
        {
            var manifest = _sessionStore.Load(sessionId);
            if (manifest != null && manifest.Segments.Count > 1)
            {
                var merger = new SessionMerger(_sessionStore, _ffprobeClient, _chapterWriter, _fileSystem, _ffmpegPath);
                var mergeResult = await merger.MergeSessionAsync(sessionId, ct: ct).ConfigureAwait(false);
                if (mergeResult.Success && !string.IsNullOrEmpty(mergeResult.MergedFilePath))
                {
                    manifest.MergedPath = mergeResult.MergedFilePath;
                    manifest.MergeStatus = "Done";

                    if (savingSettings.DeletePartsAfterMerge)
                    {
                        foreach (var seg in manifest.Segments)
                        {
                            var final = seg.FinalPath;
                            if (!string.IsNullOrEmpty(final) && _fileSystem.File.Exists(final))
                            {
                                try { _fileSystem.File.Delete(final); } catch { }
                            }
                            var ts = seg.TsPath;
                            if (!string.IsNullOrEmpty(ts) && _fileSystem.File.Exists(ts))
                            {
                                try { _fileSystem.File.Delete(ts); } catch { }
                            }
                        }
                    }

                    _sessionStore.Save(manifest);
                    return mergeResult;
                }
                else
                {
                    manifest.MergeStatus = "Failed";
                    _sessionStore.Save(manifest);
                    return mergeResult;
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }
}
