using System.Diagnostics;
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
        var titleSuffix = !string.IsNullOrWhiteSpace(manifest.Title) ? $"_{manifest.Title}" : "";
        var destination = outputFilePath ?? _fileSystem.Path.Combine(dayFolder, $"SV_{sessionId}{titleSuffix}.mkv");

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
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("concat");
            startInfo.ArgumentList.Add("-safe");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(concatListFile);

            if (_fileSystem.File.Exists(chaptersFile))
            {
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(chaptersFile);
                startInfo.ArgumentList.Add("-map_chapters");
                startInfo.ArgumentList.Add("1");
            }

            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("copy");

            var title = !string.IsNullOrWhiteSpace(manifest.Title)
                ? manifest.Title
                : $"ScreenVault session {sessionId}";
            startInfo.ArgumentList.Add("-metadata");
            startInfo.ArgumentList.Add($"title={title}");
            startInfo.ArgumentList.Add("-metadata");
            startInfo.ArgumentList.Add($"comment=ScreenVault session {sessionId}");
            startInfo.ArgumentList.Add("-metadata");
            startInfo.ArgumentList.Add($"creation_time={manifest.StartedAtUtc:yyyy-MM-ddTHH:mm:ssZ}");

            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add(tempMerged);

            Log.Information("Merging session {SessionId} ({Count} parts) into {Dest}...", sessionId, filePaths.Count, destination);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                Log.Error("FFmpeg session merge failed with code {Code}: {Error}", process.ExitCode, stderr);
                if (_fileSystem.File.Exists(tempMerged))
                {
                    _fileSystem.File.Delete(tempMerged);
                }
                return new MergeResult(false, null, $"FFmpeg merge exited with code {process.ExitCode}: {stderr}");
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
}
