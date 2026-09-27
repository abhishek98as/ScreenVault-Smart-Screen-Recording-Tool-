using System.Globalization;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Serilog;

namespace ScreenVault.Core.Sessions;

public sealed class RecoveryResultEventArgs : EventArgs
{
    public bool HadInterruptedSessions { get; }
    public int RecoveredFileCount { get; }
    public string? Message { get; }

    public RecoveryResultEventArgs(bool hadInterruptedSessions, int recoveredFileCount, string? message)
    {
        HadInterruptedSessions = hadInterruptedSessions;
        RecoveredFileCount = recoveredFileCount;
        Message = message;
    }
}

public interface IRecoveryService
{
    Task<RecoveryResultEventArgs> RunRecoveryAsync(CancellationToken ct = default);
    event EventHandler<RecoveryResultEventArgs>? RecoveryCompleted;
}

public sealed class RecoveryService : IRecoveryService
{
    private readonly ISessionStore _sessionStore;
    private readonly StorageSettings _storageSettings;
    private readonly IPostProcessor _postProcessor;
    private readonly IFfprobeClient _ffprobeClient;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly string _ffmpegPath;

    public event EventHandler<RecoveryResultEventArgs>? RecoveryCompleted;

    public RecoveryService(
        ISessionStore sessionStore,
        StorageSettings storageSettings,
        IPostProcessor postProcessor,
        IFileSystem? fileSystem = null,
        IClock? clock = null,
        string? ffmpegPath = null,
        IFfprobeClient? ffprobeClient = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _storageSettings = storageSettings ?? throw new ArgumentNullException(nameof(storageSettings));
        _postProcessor = postProcessor ?? throw new ArgumentNullException(nameof(postProcessor));
        _fileSystem = fileSystem ?? new FileSystem();
        _clock = clock ?? new SystemClock();
        _ffprobeClient = ffprobeClient ?? new FfprobeClient();

        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            var paths = new FfmpegLocator(_fileSystem).Locate();
            _ffmpegPath = paths.FfmpegPath;
        }
        else
        {
            _ffmpegPath = ffmpegPath;
        }
    }

    public async Task<RecoveryResultEventArgs> RunRecoveryAsync(CancellationToken ct = default)
    {
        Log.Information("RecoveryService: Starting background startup recovery scan...");

        // 1. Kill orphan FFmpeg processes matching our bundled path
        try
        {
            var killed = OrphanProcessKiller.KillOrphans(_ffmpegPath);
            if (killed > 0)
            {
                Log.Information("RecoveryService: Cleaned up {Killed} orphan FFmpeg process(es).", killed);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "RecoveryService: Error checking for orphan FFmpeg processes.");
        }

        var hadInterrupted = false;
        var recoveredCount = 0;
        string? timeRangeStr = null;

        // 2. Scan and mark interrupted sessions in canonical store
        try
        {
            var manifests = _sessionStore.LoadAllCanonical();
            foreach (var manifest in manifests)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if ((manifest.Status == "Recording" || manifest.Status == "Stopping") && !_sessionStore.IsLive(manifest.SessionId))
                {
                    hadInterrupted = true;

                    DateTime endedAt = manifest.StartedAtUtc;
                    if (manifest.Segments.Count > 0)
                    {
                        var lastSeg = manifest.Segments[^1];
                        if (lastSeg.EndedAtUtc.HasValue)
                        {
                            endedAt = lastSeg.EndedAtUtc.Value;
                        }
                        else
                        {
                            var fullTs = _fileSystem.Path.Combine(lastSeg.Location, lastSeg.TsPath);
                            if (_fileSystem.File.Exists(fullTs))
                            {
                                endedAt = _fileSystem.File.GetLastWriteTimeUtc(fullTs);
                            }
                        }
                    }

                    _sessionStore.Update(manifest.SessionId, m =>
                    {
                        m.Status = "Interrupted";
                        m.EndedAtUtc = endedAt;
                        return true;
                    });

                    var startLocal = manifest.StartedAtUtc.ToLocalTime();
                    var endLocal = endedAt.ToLocalTime();
                    timeRangeStr = $"{startLocal:HH:mm}\u2013{endLocal:HH:mm}";
                    Log.Information("RecoveryService: Marked session {SessionId} as Interrupted ({Range})", manifest.SessionId, timeRangeStr);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RecoveryService: Error checking session manifests.");
        }

        // 3. Scan storage locations for missing manifests and orphaned video files
        await Task.Run(async () =>
        {
            var segmentFileRegex = new Regex(@"^SV_(?<sessionId>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(?:_p(?<part>\d{1,4}))?(?:_\d+)?\.(?<ext>mkv|mp4|ts)$", RegexOptions.IgnoreCase);

            foreach (var loc in _storageSettings.Locations)
            {
                if (ct.IsCancellationRequested) break;
                if (!loc.Enabled) continue;

                var root = Environment.ExpandEnvironmentVariables(loc.Path);
                if (!_fileSystem.Directory.Exists(root)) continue;

                try
                {
                    // Clean up stale *.partial files (both day folders and .screenvault\tmp)
                    var partialFiles = _fileSystem.Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories);
                    foreach (var partial in partialFiles)
                    {
                        try
                        {
                            _fileSystem.File.Delete(partial);
                            Log.Information("RecoveryService: Removed stale partial file {File}", partial);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "RecoveryService: Could not delete partial file {File}", partial);
                        }
                    }

                    // Scan all video files in this storage location
                    var allVideoFiles = _fileSystem.Directory.GetFiles(root, "SV_*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Group files by sessionId
                    var sessionGroups = new Dictionary<string, List<(string FilePath, int Part, string Ext)>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in allVideoFiles)
                    {
                        var fileName = _fileSystem.Path.GetFileName(file);
                        var match = segmentFileRegex.Match(fileName);
                        if (!match.Success) continue;

                        var sessionId = match.Groups["sessionId"].Value;
                        var partStr = match.Groups["part"].Value;
                        var part = int.TryParse(partStr, out var p) ? p : 1;
                        var ext = match.Groups["ext"].Value.ToLowerInvariant();

                        if (!sessionGroups.TryGetValue(sessionId, out var list))
                        {
                            list = [];
                            sessionGroups[sessionId] = list;
                        }
                        list.Add((file, part, ext));
                    }

                    // Rebuild missing manifests
                    var metadataDir = StorageDirectoryHelper.GetMetadataDirectory(_fileSystem, root);
                    foreach (var (sessionId, files) in sessionGroups)
                    {
                        if (ct.IsCancellationRequested) break;

                        var manifest = _sessionStore.Load(sessionId);
                        if (manifest == null)
                        {
                            var backupManifestPath = _fileSystem.Path.Combine(metadataDir, $"SV_{sessionId}.session.json");
                            if (_fileSystem.File.Exists(backupManifestPath))
                            {
                                manifest = _sessionStore.LoadFromPath(backupManifestPath);
                                if (manifest != null)
                                {
                                    _sessionStore.Save(manifest);
                                }
                            }
                        }

                        if (manifest == null)
                        {
                            // Rebuild missing manifest from video files
                            DateTime startedUtc;
                            if (DateTime.TryParseExact(sessionId, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var localStart))
                            {
                                startedUtc = localStart.ToUniversalTime();
                            }
                            else
                            {
                                startedUtc = files.Select(f => _fileSystem.File.GetCreationTimeUtc(f.FilePath)).Min();
                            }

                            manifest = new SessionManifest
                            {
                                SessionId = sessionId,
                                StartedAtUtc = startedUtc,
                                Status = "Completed",
                                Machine = Environment.MachineName,
                                AppVersion = "1.1.0"
                            };

                            var partsGroup = files.GroupBy(f => f.Part).OrderBy(g => g.Key);
                            double cumulativeSec = 0;

                            foreach (var pg in partsGroup)
                            {
                                var partIndex = pg.Key;
                                var finalFile = pg.FirstOrDefault(f => f.Ext is "mkv" or "mp4").FilePath;
                                var tsFile = pg.FirstOrDefault(f => f.Ext == "ts").FilePath;
                                var representativeFile = finalFile ?? tsFile!;

                                var relLocation = root;
                                var relFinal = !string.IsNullOrEmpty(finalFile)
                                    ? Path.GetRelativePath(root, finalFile)
                                    : string.Empty;
                                var relTs = !string.IsNullOrEmpty(tsFile)
                                    ? Path.GetRelativePath(root, tsFile)
                                    : string.Empty;

                                double dur = 0;
                                try
                                {
                                    var probe = await _ffprobeClient.ProbeAsync(representativeFile, ct).ConfigureAwait(false);
                                    if (probe != null && probe.DurationSeconds > 0)
                                    {
                                        dur = probe.DurationSeconds;
                                    }
                                }
                                catch
                                {
                                    // Ignore probe failure
                                }

                                if (dur <= 0)
                                {
                                    var writeTime = _fileSystem.File.GetLastWriteTimeUtc(representativeFile);
                                    var createTime = _fileSystem.File.GetCreationTimeUtc(representativeFile);
                                    dur = Math.Max(1.0, (writeTime - createTime).TotalSeconds);
                                }

                                var fileBytes = _fileSystem.FileInfo.New(representativeFile).Length;

                                var segEntry = new SegmentManifestEntry
                                {
                                    Index = partIndex,
                                    Location = relLocation,
                                    TsPath = relTs,
                                    FinalPath = relFinal,
                                    Bytes = fileBytes,
                                    DurationSec = Math.Round(dur, 2),
                                    StartedAtUtc = startedUtc.AddSeconds(cumulativeSec),
                                    EndedAtUtc = startedUtc.AddSeconds(cumulativeSec + dur),
                                    Remux = !string.IsNullOrEmpty(relFinal) ? "Done" : "Pending"
                                };

                                cumulativeSec += dur;
                                manifest.Segments.Add(segEntry);
                            }

                            manifest.EndedAtUtc = startedUtc.AddSeconds(cumulativeSec);
                            _sessionStore.Save(manifest, [metadataDir]);
                            hadInterrupted = true;
                            Log.Information("RecoveryService: Rebuilt missing manifest for session {SessionId} ({Count} parts) from video files.",
                                sessionId, manifest.Segments.Count);
                        }
                    }

                    // Look for unfinalized TS files needing remux (not needed when TS is the chosen format)
                    var tsFiles = _storageSettings.OutputFormat == OutputContainerFormat.Ts
                        ? Array.Empty<string>()
                        : _fileSystem.Directory.GetFiles(root, "SV_*.ts", SearchOption.AllDirectories);
                    foreach (var ts in tsFiles)
                    {
                        // A part that is being written right now belongs to the running recording.
                        if (IsFileInUse(ts))
                        {
                            continue;
                        }

                        var mkv = _fileSystem.Path.ChangeExtension(ts, ".mkv");
                        var mp4 = _fileSystem.Path.ChangeExtension(ts, ".mp4");

                        if (_fileSystem.File.Exists(mkv) || _fileSystem.File.Exists(mp4))
                        {
                            if (!_storageSettings.KeepTsAfterRemux)
                            {
                                try { _fileSystem.File.Delete(ts); } catch { }
                            }
                            continue;
                        }

                        recoveredCount++;
                        var finalPath = _storageSettings.OutputFormat == OutputContainerFormat.Mp4 ? mp4 : mkv;

                        // Tie the job to its session so the manifest records the result.
                        var nameMatch = segmentFileRegex.Match(_fileSystem.Path.GetFileName(ts));
                        var recoveredSessionId = nameMatch.Success ? nameMatch.Groups["sessionId"].Value : null;
                        var recoveredPart = nameMatch.Success && int.TryParse(nameMatch.Groups["part"].Value, out var parsedPart) ? parsedPart : 1;

                        var job = new RemuxJob
                        {
                            TsPath = ts,
                            FinalPath = finalPath,
                            OutputFormat = _storageSettings.OutputFormat,
                            KeepTsAfterRemux = _storageSettings.KeepTsAfterRemux,
                            SessionTitle = "Recovered ScreenVault Recording",
                            SegmentStartUtc = _fileSystem.File.GetCreationTimeUtc(ts),
                            SegmentEndUtc = _fileSystem.File.GetLastWriteTimeUtc(ts),
                            SessionId = recoveredSessionId,
                            SegmentIndex = recoveredPart
                        };

                        _postProcessor.Enqueue(job);
                        Log.Information("RecoveryService: Enqueued recovered remux for {Ts}", ts);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "RecoveryService: Error scanning location {Loc}", root);
                }
            }
        }, ct).ConfigureAwait(false);

        string? notificationMessage = null;
        if (hadInterrupted || recoveredCount > 0)
        {
            notificationMessage = timeRangeStr != null
                ? $"Recovered interrupted recording ({timeRangeStr})."
                : $"Recovered {recoveredCount} interrupted recording segment(s).";

            Log.Information("RecoveryService finished: {Msg}", notificationMessage);
        }

        var result = new RecoveryResultEventArgs(hadInterrupted, recoveredCount, notificationMessage);
        RecoveryCompleted?.Invoke(this, result);
        return result;
    }

    private bool IsFileInUse(string path)
    {
        try
        {
            using var stream = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
