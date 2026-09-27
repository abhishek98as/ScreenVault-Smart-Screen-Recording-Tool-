using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
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
            // A retry after the first attempt succeeded, or the file was moved/deleted meanwhile.
            if (_fileSystem.File.Exists(job.FinalPath))
            {
                Log.Information("Remux source {TsPath} is gone but the final file exists; nothing to do.", job.TsPath);
                UpdateManifestRemuxStatus(job, "Done");
                NotifyCompleted(job, success: true);
                return;
            }

            Log.Warning("Remux source file does not exist: {TsPath}", job.TsPath);
            UpdateManifestRemuxStatus(job, "Failed");
            NotifyCompleted(job, success: false, errorMessage: "Source file not found");
            return;
        }

        if (job.OutputFormat == OutputContainerFormat.Ts)
        {
            Log.Information("Format is TS; no remux needed for {TsPath}", job.TsPath);
            var tsDuration = (await _ffprobeClient.ProbeAsync(job.TsPath, ct).ConfigureAwait(false))?.DurationSeconds;
            UpdateManifestRemuxStatus(job, "NotRequired", tsDuration);
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

            // 2. Build FFmpeg command (argument list: no quoting problems with paths or titles)
            var title = !string.IsNullOrWhiteSpace(job.SessionTitle)
                ? job.SessionTitle
                : $"ScreenVault session {job.SessionId}";

            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", job.TsPath };
            if (!string.IsNullOrEmpty(chapterFile) && _fileSystem.File.Exists(chapterFile))
            {
                args.AddRange(["-i", chapterFile, "-map", "0", "-map_chapters", "1"]);
            }
            else
            {
                args.AddRange(["-map", "0"]);
            }

            args.AddRange(["-c", "copy", "-bsf:a", "aac_adtstoasc"]);
            args.AddRange(["-metadata", $"title={title}"]);
            args.AddRange(["-metadata", $"comment=ScreenVault session {job.SessionId}"]);
            args.AddRange(["-metadata", string.Create(CultureInfo.InvariantCulture, $"creation_time={job.SegmentStartUtc:yyyy-MM-ddTHH:mm:ssZ}")]);

            if (job.OutputFormat == OutputContainerFormat.Mp4)
            {
                args.AddRange(["-movflags", "+faststart", "-f", "mp4", partialPath]);
            }
            else
            {
                args.AddRange(["-f", "matroska", partialPath]);
            }

            // 3. Run FFmpeg at BelowNormal priority
            var (exitCode, stderr) = await FfmpegRunner.RunAsync(_ffmpegPath, args, ProcessPriorityClass.BelowNormal, ct).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg remux failed with code {exitCode}: {stderr}");
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

            UpdateManifestRemuxStatus(job, "Done", remuxProbe.DurationSeconds);
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

    private void UpdateManifestRemuxStatus(RemuxJob job, string status, double? durationSec = null)
    {
        if (string.IsNullOrEmpty(job.SessionId))
        {
            return;
        }

        try
        {
            // Atomic update: while the session is still being recorded this edits the recorder's own
            // copy, so the result is not overwritten by the recorder's next save.
            _sessionStore.Update(job.SessionId, manifest =>
            {
                var segment = manifest.Segments.FirstOrDefault(s => s.Index == job.SegmentIndex);
                if (segment == null)
                {
                    return false;
                }

                segment.Remux = status;
                if (durationSec is > 0)
                {
                    segment.DurationSec = Math.Round(durationSec.Value, 2);
                }

                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update manifest status for session {SessionId}", job.SessionId);
        }
    }

    private string? ResolveSegmentPath(SegmentManifestEntry segment, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (_fileSystem.Path.IsPathRooted(path))
        {
            return path;
        }

        var location = Environment.ExpandEnvironmentVariables(segment.Location);
        return string.IsNullOrWhiteSpace(location) ? null : _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(location, path));
    }

    private void DeleteQuietly(string? path, string keep)
    {
        if (string.IsNullOrEmpty(path) || string.Equals(path, keep, StringComparison.OrdinalIgnoreCase) || !_fileSystem.File.Exists(path))
        {
            return;
        }

        try
        {
            _fileSystem.File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete merged part {Path}", path);
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
                    if (savingSettings.DeletePartsAfterMerge)
                    {
                        // Segment paths are stored relative to their storage location.
                        foreach (var seg in manifest.Segments)
                        {
                            DeleteQuietly(ResolveSegmentPath(seg, seg.FinalPath), mergeResult.MergedFilePath);
                            DeleteQuietly(ResolveSegmentPath(seg, seg.TsPath), mergeResult.MergedFilePath);
                        }
                    }

                    _sessionStore.Update(sessionId, m =>
                    {
                        m.MergedPath = mergeResult.MergedFilePath;
                        m.MergeStatus = "Done";
                        return true;
                    });
                    return mergeResult;
                }

                _sessionStore.Update(sessionId, m =>
                {
                    m.MergeStatus = "Failed";
                    return true;
                });
                return mergeResult;
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
