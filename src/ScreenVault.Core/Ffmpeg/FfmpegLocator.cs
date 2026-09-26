using System.IO.Abstractions;

namespace ScreenVault.Core.Ffmpeg;

public sealed record FfmpegBinaryPaths(string FfmpegPath, string FfprobePath);

public sealed class FfmpegLocator
{
    private readonly IFileSystem _fileSystem;

    public FfmpegLocator(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public FfmpegBinaryPaths Locate(string? customFfmpegPath = null)
    {
        // 1. Custom path from settings if provided
        if (!string.IsNullOrWhiteSpace(customFfmpegPath))
        {
            var customExpanded = Environment.ExpandEnvironmentVariables(customFfmpegPath);
            if (_fileSystem.File.Exists(customExpanded))
            {
                var dir = _fileSystem.Path.GetDirectoryName(customExpanded) ?? string.Empty;
                var probeCandidate = _fileSystem.Path.Combine(dir, "ffprobe.exe");
                var ffprobePath = _fileSystem.File.Exists(probeCandidate) ? probeCandidate : FindOnPath("ffprobe.exe");
                if (!string.IsNullOrEmpty(ffprobePath))
                {
                    return new FfmpegBinaryPaths(customExpanded, ffprobePath);
                }
            }
        }

        // 2. Bundled next to app or in ffmpeg/ subfolder
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var searchLocations = new[]
        {
            _fileSystem.Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            _fileSystem.Path.Combine(baseDir, "ffmpeg.exe"),
            _fileSystem.Path.Combine(baseDir, "..", "..", "..", "tools", "ffmpeg", "ffmpeg.exe") // dev-time fallback
        };

        foreach (var ffmpegCandidate in searchLocations)
        {
            if (_fileSystem.File.Exists(ffmpegCandidate))
            {
                var dir = _fileSystem.Path.GetDirectoryName(ffmpegCandidate) ?? string.Empty;
                var ffprobeCandidate = _fileSystem.Path.Combine(dir, "ffprobe.exe");
                if (_fileSystem.File.Exists(ffprobeCandidate))
                {
                    return new FfmpegBinaryPaths(ffmpegCandidate, ffprobeCandidate);
                }
            }
        }

        // 3. Fallback to system PATH
        var pathFfmpeg = FindOnPath("ffmpeg.exe");
        var pathFfprobe = FindOnPath("ffprobe.exe");
        if (!string.IsNullOrEmpty(pathFfmpeg) && !string.IsNullOrEmpty(pathFfprobe))
        {
            return new FfmpegBinaryPaths(pathFfmpeg, pathFfprobe);
        }

        throw new FileNotFoundException(
            "Could not locate ffmpeg.exe or ffprobe.exe. Ensure the bundled FFmpeg binaries exist in the 'ffmpeg' subfolder or are configured in Settings.");
    }

    private string? FindOnPath(string binaryName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            try
            {
                var candidate = _fileSystem.Path.Combine(entry, binaryName);
                if (_fileSystem.File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore invalid PATH entries
            }
        }

        return null;
    }
}
