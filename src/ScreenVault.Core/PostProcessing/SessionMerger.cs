using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Storage;
using Serilog;

namespace ScreenVault.Core.PostProcessing;

public sealed record MergeResult(bool Success, string? MergedFilePath, string? ErrorMessage);

public interface ISessionMerger
{
    Task<MergeResult> MergeSessionAsync(string sessionId, string? outputFilePath = null, CancellationToken ct = default);
}

public sealed class SessionMerger : ISessionMerger
{
    private readonly ISessionStore _sessionStore;
    private readonly IFfprobeClient _ffprobeClient;
    private readonly IChapterWriter _chapterWriter;
    private readonly IFileSystem _fileSystem;
    private readonly string _ffmpegPath;

    public SessionMerger(
        ISessionStore sessionStore,
        IFfprobeClient ffprobeClient,
        IChapterWriter chapterWriter,
        IFileSystem? fileSystem = null,
        string? ffmpegPath = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _ffprobeClient = ffprobeClient ?? throw new ArgumentNullException(nameof(ffprobeClient));
        _chapterWriter = chapterWriter ?? throw new ArgumentNullException(nameof(chapterWriter));
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
    }

    public async Task<MergeResult> MergeSessionAsync(string sessionId, string? outputFilePath = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var manifest = _sessionStore.Load(sessionId);
        if (manifest == null)
        {
            return new MergeResult(false, null, $"Session '{sessionId}' not found.");
        }

        if (manifest.Segments.Count == 0)
        {
            return new MergeResult(false, null, $"Session '{sessionId}' contains no recorded segments.");
        }

        // Collect all segment files (prefer finalPath if it exists, otherwise tsPath)
        var filePaths = new List<string>();
        foreach (var seg in manifest.Segments.OrderBy(s => s.Index))
        {
            string? chosenPath = null;
            if (!string.IsNullOrEmpty(seg.FinalPath))
            {
                var fullFinal = _fileSystem.Path.IsPathRooted(seg.FinalPath)
                    ? seg.FinalPath
                    : _fileSystem.Path.Combine(seg.Location, seg.FinalPath);

                if (_fileSystem.File.Exists(fullFinal))
                {
                    chosenPath = fullFinal;
                }
            }

            if (chosenPath == null && !string.IsNullOrEmpty(seg.TsPath))
            {
                var fullTs = _fileSystem.Path.IsPathRooted(seg.TsPath)
                    ? seg.TsPath
                    : _fileSystem.Path.Combine(seg.Location, seg.TsPath);

                if (_fileSystem.File.Exists(fullTs))
                {
                    chosenPath = fullTs;
                }
            }

            if (chosenPath == null)
            {
                return new MergeResult(false, null, $"Missing segment file for index {seg.Index} in session '{sessionId}'.");
            }

            filePaths.Add(chosenPath);
        }

        var primaryLocation = manifest.Segments[0].Location;
        var dayFolder = _fileSystem.Path.GetDirectoryName(filePaths[0]) ?? primaryLocation;
        var titleSuffix = SafeFileNamePart(manifest.Title);
        var destination = outputFilePath ?? _fileSystem.Path.Combine(dayFolder, $"SV_{sessionId}{(titleSuffix.Length > 0 ? "_" + titleSuffix : string.Empty)}.mkv");

        var locationRoot = StorageDirectoryHelper.GetLocationRoot(_fileSystem, destination);
        var tempDir = StorageDirectoryHelper.GetTempDirectory(_fileSystem, locationRoot);
        var concatListFile = _fileSystem.Path.Combine(tempDir, $"concat_{sessionId}_{Guid.NewGuid():N}.txt");
        var chaptersFile = _fileSystem.Path.Combine(tempDir, $"chapters_{sessionId}_{Guid.NewGuid():N}.txt");
        var tempMerged = _fileSystem.Path.Combine(tempDir, _fileSystem.Path.GetFileName(destination) + ".partial");

        try
        {
            // 1. Build ffconcat list
            var concatBuilder = new StringBuilder();
            concatBuilder.AppendLine("ffconcat version 1.0");
            foreach (var path in filePaths)
            {
                concatBuilder.Append(CultureInfo.InvariantCulture, $"file '{path.Replace("'", @"'\''")}'\n");
            }
            await _fileSystem.File.WriteAllTextAsync(concatListFile, concatBuilder.ToString(), ct).ConfigureAwait(false);

            // 2. Build FFMETADATA1 chapters file across the entire session
            var sessionDuration = manifest.EndedAtUtc.HasValue
                ? (manifest.EndedAtUtc.Value - manifest.StartedAtUtc).TotalSeconds
                : (DateTime.UtcNow - manifest.StartedAtUtc).TotalSeconds;

            if (sessionDuration <= 0) sessionDuration = 3600;

            _chapterWriter.WriteSessionChapters($"ScreenVault session {sessionId}", manifest.Markers, chaptersFile);

            // 3. Run ffmpeg concat
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", concatListFile };

            if (_fileSystem.File.Exists(chaptersFile))
            {
                args.AddRange(["-i", chaptersFile, "-map_chapters", "1"]);
            }

            args.AddRange(["-map", "0", "-c", "copy"]);

            var title = !string.IsNullOrWhiteSpace(manifest.Title)
                ? manifest.Title
                : $"ScreenVault session {sessionId}";
            args.AddRange(["-metadata", $"title={title}"]);
            args.AddRange(["-metadata", $"comment=ScreenVault session {sessionId}"]);
            args.AddRange(["-metadata", string.Create(CultureInfo.InvariantCulture, $"creation_time={manifest.StartedAtUtc:yyyy-MM-ddTHH:mm:ssZ}")]);

            // The temporary name ends in ".partial", so the container must be named explicitly.
            args.AddRange(["-f", "matroska", "-y", tempMerged]);

            Log.Information("Merging session {SessionId} ({Count} parts) into {Dest}...", sessionId, filePaths.Count, destination);

            var (exitCode, stderr) = await FfmpegRunner.RunAsync(_ffmpegPath, args, ct: ct).ConfigureAwait(false);
            if (exitCode != 0)
            {
                Log.Error("FFmpeg session merge failed with code {Code}: {Error}", exitCode, stderr);
                if (_fileSystem.File.Exists(tempMerged))
                {
                    _fileSystem.File.Delete(tempMerged);
                }
                return new MergeResult(false, null, $"FFmpeg merge exited with code {exitCode}: {stderr}");
            }

            // 4. Validate output with ffprobe
            var probeResult = await _ffprobeClient.ProbeAsync(tempMerged, ct).ConfigureAwait(false);
            if (probeResult == null || !probeResult.HasVideoStream)
            {
                Log.Error("Merged file failed ffprobe validation.");
                if (_fileSystem.File.Exists(tempMerged))
                {
                    _fileSystem.File.Delete(tempMerged);
                }
                return new MergeResult(false, null, "Merged file is missing video stream or failed ffprobe validation.");
            }

            // Verify duration within ± 2 seconds
            var expectedDuration = manifest.Segments.Sum(s => s.DurationSec ?? 0.0);
            if (expectedDuration <= 0 && manifest.EndedAtUtc.HasValue)
            {
                expectedDuration = (manifest.EndedAtUtc.Value - manifest.StartedAtUtc).TotalSeconds;
            }

            if (expectedDuration > 0 && Math.Abs(probeResult.DurationSeconds - expectedDuration) > 2.0)
            {
                Log.Warning("Merged file duration discrepancy: Expected={Expected:F1}s, Probed={Probed:F1}s (diff > 2s)",
                    expectedDuration, probeResult.DurationSeconds);
            }

            // 5. Atomic rename to destination
            if (_fileSystem.File.Exists(destination))
            {
                _fileSystem.File.Delete(destination);
            }
            _fileSystem.File.Move(tempMerged, destination);

            Log.Information("Session {SessionId} successfully merged into {Dest}", sessionId, destination);
            return new MergeResult(true, destination, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error merging session {SessionId}", sessionId);
            if (_fileSystem.File.Exists(tempMerged))
            {
                try { _fileSystem.File.Delete(tempMerged); } catch { }
            }
            return new MergeResult(false, null, ex.Message);
        }
        finally
        {
            try
            {
                if (_fileSystem.File.Exists(concatListFile)) _fileSystem.File.Delete(concatListFile);
                if (_fileSystem.File.Exists(chaptersFile)) _fileSystem.File.Delete(chaptersFile);
            }
            catch { }
        }
    }

    /// <summary>Turns a user title into something safe for a file name ("" when there is none).</summary>
    internal static string SafeFileNamePart(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var cleaned = new string(title.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim(' ', '.');
        if (cleaned.Length > 60)
        {
            cleaned = cleaned[..60].TrimEnd(' ', '.');
        }

        return cleaned;
    }
}
