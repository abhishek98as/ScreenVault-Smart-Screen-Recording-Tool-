using System.IO.Abstractions;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.Storage;

public interface IRetentionService : IDisposable
{
    void Start();
    Task<int> RunCleanupAsync(CancellationToken ct = default);
}

public sealed class RetentionService : IRetentionService
{
    private readonly ISessionStore _sessionStore;
    private readonly ISettingsService _settingsService;
    private readonly IFileSystem _fileSystem;
    private readonly System.Threading.Timer _timer;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);
    private bool _isDisposed;

    public RetentionService(
        ISessionStore sessionStore,
        ISettingsService settingsService,
        IFileSystem? fileSystem = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _fileSystem = fileSystem ?? new FileSystem();
        _timer = new System.Threading.Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        // Run initial retention check after 1 minute, then every 6 hours
        _timer.Change(TimeSpan.FromMinutes(1), _checkInterval);
    }

    private void OnTimerTick(object? state)
    {
        // Timer callbacks must never throw: an exception here would take the whole app down.
        try
        {
            _ = RunCleanupAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Retention: clean-up run failed.");
        }
    }

    public Task<int> RunCleanupAsync(CancellationToken ct = default)
    {
        var retention = _settingsService.Current.Storage.Retention;
        if (!retention.Enabled || retention.KeepDays <= 0)
        {
            return Task.FromResult(0);
        }

        var cutoffUtc = DateTime.UtcNow.AddDays(-retention.KeepDays);
        var manifests = _sessionStore.LoadAllCanonical();
        var deletedCount = 0;

        foreach (var manifest in manifests.OrderBy(m => m.StartedAtUtc))
        {
            if (ct.IsCancellationRequested) break;

            // Skip active sessions
            if (string.Equals(manifest.Status, "Recording", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check if older than cutoff
            if (manifest.StartedAtUtc >= cutoffUtc)
            {
                continue;
            }

            // Check protection
            if (manifest.Protected)
            {
                Log.Debug("Retention: Skipping protected session {SessionId}", manifest.SessionId);
                continue;
            }

            if (retention.ProtectSessionsWithMarkers && manifest.Markers.Count > 0)
            {
                Log.Debug("Retention: Skipping session with markers {SessionId}", manifest.SessionId);
                continue;
            }

            Log.Information("Retention: Cleaning up expired session {SessionId} (Recorded {Started})",
                manifest.SessionId, manifest.StartedAtUtc);

            // Delete segment files and the merged file, remembering the folders they were in
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in manifest.Segments)
            {
                AddFolder(folders, DeleteFileIfExists(seg.FinalPath, seg.Location));
                AddFolder(folders, DeleteFileIfExists(seg.TsPath, seg.Location));
            }

            if (!string.IsNullOrWhiteSpace(manifest.MergedPath))
            {
                AddFolder(folders, DeleteFileIfExists(Environment.ExpandEnvironmentVariables(manifest.MergedPath), string.Empty));
            }

            // Remove day folders that are now completely empty. Never delete recursively: the user
            // may keep their own files (exported clips, notes) next to the recordings.
            foreach (var folder in folders)
            {
                try
                {
                    if (_fileSystem.Directory.Exists(folder) && !_fileSystem.Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        _fileSystem.Directory.Delete(folder, recursive: false);
                        Log.Information("Retention: Removed empty day directory {Dir}", folder);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Retention: Could not remove directory {Dir}", folder);
                }
            }

            // Remove both canonical and backup manifests
            try
            {
                var mirrorDirs = manifest.Segments
                    .Select(s => StorageDirectoryHelper.GetMetadataDirectory(_fileSystem, s.Location))
                    .Distinct()
                    .ToList();
                _sessionStore.Delete(manifest.SessionId, mirrorDirs);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Retention: Could not delete session manifests for {SessionId}", manifest.SessionId);
            }

            deletedCount++;
        }

        if (deletedCount > 0)
        {
            Log.Information("Retention: Completed cleanup. Removed {Count} expired sessions.", deletedCount);
        }

        return Task.FromResult(deletedCount);
    }

    /// <summary>Deletes the file if it exists and returns the folder it was in (or null).</summary>
    private string? DeleteFileIfExists(string? relativeOrFullPath, string baseLocation)
    {
        if (string.IsNullOrWhiteSpace(relativeOrFullPath)) return null;

        if (!_fileSystem.Path.IsPathRooted(relativeOrFullPath) && string.IsNullOrWhiteSpace(baseLocation)) return null;

        var fullPath = _fileSystem.Path.IsPathRooted(relativeOrFullPath)
            ? relativeOrFullPath
            : _fileSystem.Path.Combine(Environment.ExpandEnvironmentVariables(baseLocation), relativeOrFullPath);

        try
        {
            if (_fileSystem.File.Exists(fullPath))
            {
                var size = _fileSystem.FileInfo.New(fullPath).Length;
                _fileSystem.File.Delete(fullPath);
                Log.Information("Retention: Deleted {Path} ({Bytes} bytes)", fullPath, size);
                return _fileSystem.Path.GetDirectoryName(fullPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Retention: Failed to delete file {Path}", fullPath);
        }

        return null;
    }

    private static void AddFolder(HashSet<string> folders, string? folder)
    {
        if (!string.IsNullOrEmpty(folder))
        {
            folders.Add(folder);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _timer.Dispose();
    }
}
