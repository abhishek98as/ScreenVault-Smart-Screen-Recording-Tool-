using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.PostProcessing;

public sealed record ClipExportOptions(
    string SessionId,
    double StartOffsetSec,
    double EndOffsetSec,
    string DestinationPath,
    OutputContainerFormat Format = OutputContainerFormat.Mp4,
    bool PreciseReencode = false);

public sealed record ClipExportResult(bool Success, string? FilePath, string? ErrorMessage);

public interface IClipExporter
{
    Task<ClipExportResult> ExportClipAsync(ClipExportOptions options, IProgress<double>? progress = null, CancellationToken ct = default);
}

public sealed class ClipExporter : IClipExporter
{
    private readonly ISessionStore _sessionStore;
    private readonly IFileSystem _fileSystem;
    private readonly string _ffmpegPath;

    public ClipExporter(
        ISessionStore sessionStore,
        IFileSystem? fileSystem = null,
        string? ffmpegPath = null)
    {
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
    }

    public async Task<ClipExportResult> ExportClipAsync(ClipExportOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var manifest = _sessionStore.Load(options.SessionId);
        if (manifest == null)
        {
            return new ClipExportResult(false, null, $"Session '{options.SessionId}' not found.");
        }

        if (options.StartOffsetSec < 0 || options.EndOffsetSec <= options.StartOffsetSec)
        {
            return new ClipExportResult(false, null, "Invalid start or end offset specified.");
        }

        var expandedDest = Environment.ExpandEnvironmentVariables(options.DestinationPath);
        var fullDest = _fileSystem.Path.GetFullPath(expandedDest);
        var destDir = _fileSystem.Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(destDir) && !_fileSystem.Directory.Exists(destDir))
        {
            _fileSystem.Directory.CreateDirectory(destDir);
        }

        // Work next to the destination (same drive, so the final rename is instant) instead of
        // creating ScreenVault folders in whatever folder the user picked.
        var partialPath = fullDest + ".partial";
        var tempConcatFile = _fileSystem.Path.Combine(Path.GetTempPath(), $"sv_clip_concat_{Guid.NewGuid():N}.txt");
        var containerFormat = options.Format switch
        {
            OutputContainerFormat.Mkv => "matroska",
            OutputContainerFormat.Ts => "mpegts",
            _ => "mp4"
        };

        try
        {
            progress?.Report(0.1);

            // Check if verified merged file exists
            var fullMerged = !string.IsNullOrEmpty(manifest.MergedPath)
                ? _fileSystem.Path.GetFullPath(Environment.ExpandEnvironmentVariables(manifest.MergedPath))
                : null;
            if (!string.IsNullOrEmpty(fullMerged) && _fileSystem.File.Exists(fullMerged))
            {
                var args = new List<string>
                {
                    "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                    "-ss", options.StartOffsetSec.ToString("F3", CultureInfo.InvariantCulture),
                    "-to", options.EndOffsetSec.ToString("F3", CultureInfo.InvariantCulture),
                    "-i", fullMerged
                };
                AddOutputArguments(args, options, containerFormat, partialPath);

                var (exitCode, stderr) = await FfmpegRunner.RunAsync(_ffmpegPath, args, ct: ct).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    DeleteQuietly(partialPath);
                    return new ClipExportResult(false, null, $"FFmpeg clip export failed: {stderr}");
                }
            }
            else
            {
                // Multi-segment range export via ffconcat
                var concatBuilder = new StringBuilder();
                concatBuilder.AppendLine("ffconcat version 1.0");

                double currentOffset = 0.0;
                var segmentsUsed = 0;

                foreach (var seg in manifest.Segments.OrderBy(s => s.Index))
                {
                    var segDuration = seg.DurationSec ??
                        (seg.EndedAtUtc.HasValue ? (seg.EndedAtUtc.Value - seg.StartedAtUtc).TotalSeconds : 0.0);

                    if (segDuration <= 0) segDuration = 600.0; // fallback

                    var segStart = currentOffset;
                    var segEnd = currentOffset + segDuration;

                    if (segEnd > options.StartOffsetSec && segStart < options.EndOffsetSec)
                    {
                        var expLocation = _fileSystem.Path.GetFullPath(Environment.ExpandEnvironmentVariables(seg.Location));
                        var fullFinal = !string.IsNullOrEmpty(seg.FinalPath)
                            ? (_fileSystem.Path.IsPathRooted(seg.FinalPath) ? seg.FinalPath : _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(expLocation, seg.FinalPath)))
                            : null;
                        var fullTs = !string.IsNullOrEmpty(seg.TsPath)
                            ? (_fileSystem.Path.IsPathRooted(seg.TsPath) ? seg.TsPath : _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(expLocation, seg.TsPath)))
                            : null;
                        var segFile = fullFinal != null && _fileSystem.File.Exists(fullFinal) ? fullFinal : fullTs;

                        if (!string.IsNullOrEmpty(segFile) && _fileSystem.File.Exists(segFile))
                        {
                            var inPoint = Math.Max(0.0, options.StartOffsetSec - segStart);
                            var outPoint = Math.Min(segDuration, options.EndOffsetSec - segStart);

                            concatBuilder.Append(CultureInfo.InvariantCulture, $"file '{segFile.Replace("'", @"'\''")}'\n");
                            concatBuilder.Append(CultureInfo.InvariantCulture, $"inpoint {inPoint:F3}\n");
                            concatBuilder.Append(CultureInfo.InvariantCulture, $"outpoint {outPoint:F3}\n");
                            segmentsUsed++;
                        }
                    }

                    currentOffset += segDuration;
                }

                if (segmentsUsed == 0)
                {
                    return new ClipExportResult(false, null, "No segment files found for specified clip range.");
                }

                await _fileSystem.File.WriteAllTextAsync(tempConcatFile, concatBuilder.ToString(), ct).ConfigureAwait(false);

                var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", tempConcatFile };
                AddOutputArguments(args, options, containerFormat, partialPath);

                var (exitCode, stderr) = await FfmpegRunner.RunAsync(_ffmpegPath, args, ct: ct).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    DeleteQuietly(partialPath);
                    return new ClipExportResult(false, null, $"FFmpeg clip export failed: {stderr}");
                }
            }

            progress?.Report(0.9);

            if (_fileSystem.File.Exists(fullDest))
            {
                _fileSystem.File.Delete(fullDest);
            }
            _fileSystem.File.Move(partialPath, fullDest);

            progress?.Report(1.0);
            Log.Information("Clip successfully exported to {Path}", fullDest);
            return new ClipExportResult(true, fullDest, null);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Clip export for session {SessionId} was cancelled.", options.SessionId);
            DeleteQuietly(partialPath);
            return new ClipExportResult(false, null, "Export cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error exporting clip for session {SessionId}", options.SessionId);
            DeleteQuietly(partialPath);
            return new ClipExportResult(false, null, ex.Message);
        }
        finally
        {
            DeleteQuietly(tempConcatFile);
        }
    }

    private static void AddOutputArguments(List<string> args, ClipExportOptions options, string containerFormat, string outputPath)
    {
        if (options.PreciseReencode)
        {
            args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-c:a", "aac"]);
        }
        else
        {
            args.AddRange(["-c", "copy"]);
        }

        if (options.Format == OutputContainerFormat.Mp4)
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        // The temporary file ends in ".partial", so FFmpeg cannot guess the container from it.
        args.AddRange(["-f", containerFormat, outputPath]);
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            if (_fileSystem.File.Exists(path))
            {
                _fileSystem.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Could not delete temporary file {Path}", path);
        }
    }
}
