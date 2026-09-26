using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using ScreenVault.Core.Sessions;

namespace ScreenVault.Core.PostProcessing;

public interface IChapterWriter
{
    string? WriteSegmentChapters(
        string sessionTitle,
        DateTime segmentStartUtc,
        DateTime segmentEndUtc,
        IReadOnlyList<MarkerEntry> markers,
        string outputPath);

    string WriteSessionChapters(
        string sessionTitle,
        IReadOnlyList<MarkerEntry> markers,
        string outputPath);
}

public sealed class ChapterWriter : IChapterWriter
{
    private readonly IFileSystem _fileSystem;

    public ChapterWriter(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public string? WriteSegmentChapters(
        string sessionTitle,
        DateTime segmentStartUtc,
        DateTime segmentEndUtc,
        IReadOnlyList<MarkerEntry> markers,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(outputPath);

        var segmentMarkers = markers
            .Where(m => m.AtUtc >= segmentStartUtc && m.AtUtc <= segmentEndUtc)
            .OrderBy(m => m.AtUtc)
            .ToList();

        if (segmentMarkers.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine(";FFMETADATA1");
        sb.AppendLine(CultureInfo.InvariantCulture, $"title={EscapeMetadata(sessionTitle)}");

        for (var i = 0; i < segmentMarkers.Count; i++)
        {
            var m = segmentMarkers[i];
            var startMs = (long)Math.Max(0, (m.AtUtc - segmentStartUtc).TotalMilliseconds);
            long endMs;

            if (i + 1 < segmentMarkers.Count)
            {
                var nextStart = (long)Math.Max(0, (segmentMarkers[i + 1].AtUtc - segmentStartUtc).TotalMilliseconds);
                endMs = Math.Max(startMs + 1000, nextStart);
            }
            else
            {
                var segDurationMs = (long)Math.Max(0, (segmentEndUtc - segmentStartUtc).TotalMilliseconds);
                endMs = Math.Max(startMs + 1000, Math.Max(startMs + 1000, segDurationMs));
            }

            sb.AppendLine("[CHAPTER]");
            sb.AppendLine("TIMEBASE=1/1000");
            sb.AppendLine(CultureInfo.InvariantCulture, $"START={startMs}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"END={endMs}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"title={EscapeMetadata(m.Note)}");
        }

        var dir = _fileSystem.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !_fileSystem.Directory.Exists(dir))
        {
            _fileSystem.Directory.CreateDirectory(dir);
        }

        _fileSystem.File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        return outputPath;
    }

    public string WriteSessionChapters(
        string sessionTitle,
        IReadOnlyList<MarkerEntry> markers,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(outputPath);

        var sorted = markers.OrderBy(m => m.OffsetSec).ToList();

        var sb = new StringBuilder();
        sb.AppendLine(";FFMETADATA1");
        sb.AppendLine(CultureInfo.InvariantCulture, $"title={EscapeMetadata(sessionTitle)}");

        for (var i = 0; i < sorted.Count; i++)
        {
            var m = sorted[i];
            var startMs = (long)(m.OffsetSec * 1000.0);
            long endMs;

            if (i + 1 < sorted.Count)
            {
                var nextMs = (long)(sorted[i + 1].OffsetSec * 1000.0);
                endMs = Math.Max(startMs + 1000, nextMs);
            }
            else
            {
                endMs = startMs + 1000;
            }

            sb.AppendLine("[CHAPTER]");
            sb.AppendLine("TIMEBASE=1/1000");
            sb.AppendLine(CultureInfo.InvariantCulture, $"START={startMs}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"END={endMs}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"title={EscapeMetadata(m.Note)}");
        }

        var dir = _fileSystem.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !_fileSystem.Directory.Exists(dir))
        {
            _fileSystem.Directory.CreateDirectory(dir);
        }

        _fileSystem.File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        return outputPath;
    }

    private static string EscapeMetadata(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '=':
                case ';':
                case '#':
                case '\\':
                    sb.Append('\\').Append(c);
                    break;
                case '\r':
                    break;
                case '\n':
                    sb.Append(@"\n");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
