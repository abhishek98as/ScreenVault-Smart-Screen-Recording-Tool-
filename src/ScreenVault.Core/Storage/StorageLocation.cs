using System.IO.Abstractions;
using ScreenVault.Core.Sessions;
using Serilog;

namespace ScreenVault.Core.Storage;

public enum StorageLocationState
{
    Healthy,
    Low,
    Failed,
    Disabled
}

public sealed class StorageLocationInfo
{
    public string Path { get; }
    public string ExpandedPath { get; }
    public int MinFreeGb { get; }
    public bool Enabled { get; }
    public StorageLocationState State { get; set; } = StorageLocationState.Healthy;
    public long AvailableFreeBytes { get; set; }
    public DateTime LastProbeUtc { get; set; }
    public DateTime? RetryAtUtc { get; set; }

    public StorageLocationInfo(string path, int minFreeGb, bool enabled)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        var expanded = Environment.ExpandEnvironmentVariables(path);
        ExpandedPath = System.IO.Path.IsPathRooted(expanded) ? System.IO.Path.GetFullPath(expanded) : expanded;
        MinFreeGb = minFreeGb;
        Enabled = enabled;
    }
}

public static class StorageDirectoryHelper
{
    public static string GetMetadataDirectory(IFileSystem fileSystem, string storageRoot)
    {
        var expanded = Environment.ExpandEnvironmentVariables(storageRoot);
        var fullRoot = fileSystem.Path.GetFullPath(expanded);
        var svDir = fileSystem.Path.Combine(fullRoot, ".screenvault");
        var sessionsDir = fileSystem.Path.Combine(svDir, "sessions");
        if (!fileSystem.Directory.Exists(sessionsDir))
        {
            fileSystem.Directory.CreateDirectory(sessionsDir);
            try
            {
                var dirInfo = fileSystem.DirectoryInfo.New(svDir);
                dirInfo.Attributes |= FileAttributes.Hidden;
            }
            catch
            {
                // Ignore if setting hidden attribute fails
            }
        }
        return sessionsDir;
    }

    public static string GetTempDirectory(IFileSystem fileSystem, string storageRoot)
    {
        var expanded = Environment.ExpandEnvironmentVariables(storageRoot);
        var fullRoot = fileSystem.Path.GetFullPath(expanded);
        var svDir = fileSystem.Path.Combine(fullRoot, ".screenvault");
        var tmpDir = fileSystem.Path.Combine(svDir, "tmp");
        if (!fileSystem.Directory.Exists(tmpDir))
        {
            fileSystem.Directory.CreateDirectory(tmpDir);
            try
            {
                var dirInfo = fileSystem.DirectoryInfo.New(svDir);
                dirInfo.Attributes |= FileAttributes.Hidden;
            }
            catch
            {
                // Ignore
            }
        }
        return tmpDir;
    }

    public static void CleanupTempDirectory(IFileSystem fileSystem, string storageRoot, TimeSpan olderThan)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(storageRoot);
            if (!System.IO.Path.IsPathRooted(expanded)) return;
            var fullRoot = fileSystem.Path.GetFullPath(expanded);
            var tmpDir = fileSystem.Path.Combine(fullRoot, ".screenvault", "tmp");
            if (!fileSystem.Directory.Exists(tmpDir)) return;

            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var file in fileSystem.Directory.GetFiles(tmpDir))
            {
                try
                {
                    if (fileSystem.File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        fileSystem.File.Delete(file);
                        Log.Information("Cleaned up old temporary file: {File}", file);
                    }
                }
                catch
                {
                    // In-use files will throw IOException and be safely ignored
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error cleaning up temporary directory for {StorageRoot}", storageRoot);
        }
    }

    public static string GetLocationRoot(IFileSystem fileSystem, string filePath, IEnumerable<string>? configuredLocations = null)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(filePath);
        var fullPath = fileSystem.Path.GetFullPath(expandedPath);

        if (configuredLocations != null)
        {
            foreach (var loc in configuredLocations)
            {
                var expandedLoc = Environment.ExpandEnvironmentVariables(loc);
                if (fileSystem.Path.IsPathRooted(expandedLoc))
                {
                    var fullLoc = fileSystem.Path.GetFullPath(expandedLoc);
                    if (fullPath.StartsWith(fullLoc, StringComparison.OrdinalIgnoreCase))
                    {
                        return fullLoc;
                    }
                }
            }
        }

        var dir = fileSystem.Path.GetDirectoryName(fullPath);
        if (dir != null)
        {
            var parent = fileSystem.Directory.GetParent(dir);
            if (parent != null)
            {
                return parent.FullName;
            }
            return dir;
        }

        return fileSystem.Path.GetPathRoot(fullPath) ?? fullPath;
    }

    public static void MigrateLegacyFolder(IFileSystem fileSystem, string storageRoot, ISessionStore sessionStore)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(storageRoot);
            if (!fileSystem.Path.IsPathRooted(expanded)) return;
            var fullRoot = fileSystem.Path.GetFullPath(expanded);
            if (!fileSystem.Directory.Exists(fullRoot)) return;

            var metadataDir = GetMetadataDirectory(fileSystem, fullRoot);

            // 1. Move any *.session.json files from day folders to .screenvault\sessions\
            var dayDirs = fileSystem.Directory.GetDirectories(fullRoot);
            foreach (var dayDir in dayDirs)
            {
                var dirName = fileSystem.Path.GetFileName(dayDir);
                if (dirName.Equals(".screenvault", StringComparison.OrdinalIgnoreCase)) continue;

                var sessionFiles = fileSystem.Directory.GetFiles(dayDir, "*.json")
                    .Where(f => f.EndsWith(".session.json", StringComparison.OrdinalIgnoreCase) ||
                                fileSystem.Path.GetFileName(f).StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var sessionFile in sessionFiles)
                {
                    try
                    {
                        var fileName = fileSystem.Path.GetFileName(sessionFile);
                        var targetPath = fileSystem.Path.Combine(metadataDir, fileName);

                        var manifest = sessionStore.LoadFromPath(sessionFile);
                        if (manifest != null)
                        {
                            sessionStore.Save(manifest, [metadataDir]);
                        }

                        if (!fileSystem.File.Exists(targetPath))
                        {
                            fileSystem.File.Move(sessionFile, targetPath);
                        }
                        else
                        {
                            fileSystem.File.Delete(sessionFile);
                        }

                        Log.Information("Migrated legacy session manifest from {Old} to {New}", sessionFile, targetPath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to migrate legacy session file {File}", sessionFile);
                    }
                }

                // 2. Check for markers.txt in day folder
                var markersTxtPath = fileSystem.Path.Combine(dayDir, "markers.txt");
                if (fileSystem.File.Exists(markersTxtPath))
                {
                    try
                    {
                        var lines = fileSystem.File.ReadAllLines(markersTxtPath);
                        var daySessionFiles = fileSystem.Directory.GetFiles(metadataDir, $"SV_{dirName}_*.json");
                        if (daySessionFiles.Length == 0)
                        {
                            // Try loading from canonical
                            daySessionFiles = [.. sessionStore.LoadAllCanonical()
                                .Where(s => s.SessionId.StartsWith(dirName, StringComparison.OrdinalIgnoreCase))
                                .Select(s => sessionStore.GetCanonicalPath(s.SessionId))];
                        }

                        foreach (var sessionPath in daySessionFiles)
                        {
                            var manifest = sessionStore.LoadFromPath(sessionPath);
                            if (manifest == null) continue;

                            var changed = false;
                            foreach (var line in lines)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                // Line format: HH:mm:ss  [tag]  note
                                var parts = line.Split("  ", 3, StringSplitOptions.RemoveEmptyEntries);
                                var note = parts.Length == 3 ? parts[2].Trim() : line.Trim();
                                var kind = parts.Length >= 2 && parts[1].StartsWith('[') && parts[1].EndsWith(']')
                                    ? parts[1].Trim('[', ']')
                                    : "User";

                                if (!manifest.Markers.Any(m => string.Equals(m.Note, note, StringComparison.OrdinalIgnoreCase)))
                                {
                                    manifest.Markers.Add(new MarkerEntry
                                    {
                                        AtUtc = manifest.StartedAtUtc,
                                        OffsetSec = 0,
                                        Note = note,
                                        Kind = kind
                                    });
                                    changed = true;
                                }
                            }

                            if (changed)
                            {
                                sessionStore.Save(manifest, [metadataDir]);
                            }
                        }

                        fileSystem.File.Delete(markersTxtPath);
                        Log.Information("Migrated and deleted legacy markers.txt in {Dir}", dayDir);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to migrate legacy markers.txt in {Dir}", dayDir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error checking legacy folders in {StorageRoot}", storageRoot);
        }
    }
}

public interface IDiskSpaceProbe
{
    bool IsReady(string path);
    long GetAvailableFreeSpace(string path);
    bool TestWriteAccess(string path);
}

public sealed class SystemDiskSpaceProbe : IDiskSpaceProbe
{
    private readonly IFileSystem _fileSystem;

    public SystemDiskSpaceProbe(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public bool IsReady(string path)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            var root = _fileSystem.Path.GetPathRoot(expanded);
            if (string.IsNullOrEmpty(root)) return false;

            // DriveInfo only understands drive letters; network shares (\\server\share) are
            // ready when the share can be reached.
            if (IsUncRoot(root))
            {
                return _fileSystem.Directory.Exists(root);
            }

            var drive = _fileSystem.DriveInfo.New(root);
            return drive.IsReady;
        }
        catch
        {
            return false;
        }
    }

    public long GetAvailableFreeSpace(string path)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            var root = _fileSystem.Path.GetPathRoot(expanded);
            if (string.IsNullOrEmpty(root)) return 0;

            if (IsUncRoot(root))
            {
                var share = root.EndsWith('\\') ? root : root + "\\";
                return GetDiskFreeSpaceEx(share, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : 0;
            }

            var drive = _fileSystem.DriveInfo.New(root);
            return drive.AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error getting free space for path {Path}", path);
            return 0;
        }
    }

    private static bool IsUncRoot(string root) =>
        root.StartsWith(@"\\", StringComparison.Ordinal) && !root.StartsWith(@"\\?\", StringComparison.Ordinal);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    public bool TestWriteAccess(string path)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            if (!_fileSystem.Directory.Exists(expanded))
            {
                _fileSystem.Directory.CreateDirectory(expanded);
            }

            var testFile = _fileSystem.Path.Combine(expanded, $".sv_write_test_{Guid.NewGuid():N}.tmp");
            using (var stream = _fileSystem.FileStream.New(testFile, FileMode.CreateNew, FileAccess.Write))
            {
                stream.WriteByte(0x42);
                stream.Flush(flushToDisk: true);
            }

            _fileSystem.File.Delete(testFile);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Write test failed for path {Path}", path);
            return false;
        }
    }
}
