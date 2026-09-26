using System.Globalization;
using System.IO.Abstractions;

namespace ScreenVault.Core.Output;

public static class SegmentNaming
{
    public static string FormatSessionId(DateTime sessionStartLocal) =>
        sessionStartLocal.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    public static string FormatDayFolder(DateTime dateLocal) =>
        dateLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string GenerateSegmentPath(
        IFileSystem fileSystem,
        string locationDirectory,
        DateTime sessionStartLocal,
        DateTime segmentStartLocal,
        int partIndex,
        string extension = ".ts")
    {
        var expandedLocation = Environment.ExpandEnvironmentVariables(locationDirectory);
        if (!fileSystem.Path.IsPathRooted(expandedLocation))
        {
            throw new ArgumentException($"Location path '{locationDirectory}' (expanded to '{expandedLocation}') is not rooted.", nameof(locationDirectory));
        }
        var dayFolder = segmentStartLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var targetDir = fileSystem.Path.Combine(expandedLocation, dayFolder);

        if (!fileSystem.Directory.Exists(targetDir))
        {
            fileSystem.Directory.CreateDirectory(targetDir);
        }

        var sessionId = FormatSessionId(sessionStartLocal);
        var baseFileName = $"SV_{sessionId}_p{partIndex:000}{extension}";
        var candidatePath = fileSystem.Path.Combine(targetDir, baseFileName);

        if (!fileSystem.File.Exists(candidatePath))
        {
            return candidatePath;
        }

        // Collision avoidance
        var attempt = 2;
        while (true)
        {
            var altName = $"SV_{sessionId}_p{partIndex:000}_{attempt}{extension}";
            var altPath = fileSystem.Path.Combine(targetDir, altName);
            if (!fileSystem.File.Exists(altPath))
            {
                return altPath;
            }
            attempt++;
        }
    }
}
