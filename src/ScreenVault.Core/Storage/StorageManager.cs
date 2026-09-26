using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.Storage;

public sealed class StorageSwitchRequestedEventArgs : EventArgs
{
    public string NewLocation { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed record StorageStatus(
    IReadOnlyList<StorageLocationInfo> Locations,
    string ActiveLocationPath,
    bool IsInEmergencyMode);

public interface IStorageManager
{
    string SelectLocationForNewSegment(long estimatedBytes);
    void ReportWriteFailure(string locationPath, Exception exception);
    StorageStatus GetStatus();
    event EventHandler<StorageSwitchRequestedEventArgs>? SwitchRequested;
}

public sealed class StorageManager : IStorageManager, IDisposable
{
    private const long OneGb = 1024L * 1024L * 1024L;
    private const long FailbackHysteresisBytes = 10L * OneGb;
    private const long HardFloorDataDriveBytes = 1L * OneGb;
    private const long HardFloorSystemDriveBytes = 5L * OneGb;

    private readonly IDiskSpaceProbe _probe;
    private readonly StorageSettings _settings;
    private readonly List<StorageLocationInfo> _locations = [];
    private readonly object _lock = new();
    private readonly System.Threading.Timer _probeTimer;

    private StorageLocationInfo? _activeLocation;
    private bool _emergencyMode;
    private bool _isDisposed;

    public event EventHandler<StorageSwitchRequestedEventArgs>? SwitchRequested;

    public StorageManager(StorageSettings settings, IDiskSpaceProbe? probe = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _probe = probe ?? new SystemDiskSpaceProbe();

        foreach (var locConfig in _settings.Locations)
        {
            var expanded = Environment.ExpandEnvironmentVariables(locConfig.Path);
            if (!Path.IsPathRooted(expanded))
            {
                Log.Error("Storage location path '{Path}' expanded to '{Expanded}' is not a rooted absolute path. Rejecting location.", locConfig.Path, expanded);
                var invalidLoc = new StorageLocationInfo(locConfig.Path, locConfig.MinFreeGb, false)
                {
                    State = StorageLocationState.Failed
                };
                _locations.Add(invalidLoc);
                continue;
            }

            _locations.Add(new StorageLocationInfo(locConfig.Path, locConfig.MinFreeGb, locConfig.Enabled));
        }

        ProbeAllLocations();
        _probeTimer = new System.Threading.Timer(_ => ProbeAllLocations(), null, 10000, 10000);
    }

    public string SelectLocationForNewSegment(long estimatedBytes)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            for (var i = 0; i < _locations.Count; i++)
            {
                var loc = _locations[i];
                if (!loc.Enabled) continue;

                // Check retry if failed
                if (loc.State == StorageLocationState.Failed)
                {
                    if (loc.RetryAtUtc.HasValue && now < loc.RetryAtUtc.Value)
                    {
                        continue;
                    }
                    loc.State = StorageLocationState.Healthy;
                }

                if (!_probe.IsReady(loc.ExpandedPath))
                {
                    loc.State = StorageLocationState.Failed;
                    loc.RetryAtUtc = now.AddSeconds(60);
                    continue;
                }

                var free = _probe.GetAvailableFreeSpace(loc.ExpandedPath);
                loc.AvailableFreeBytes = free;
                loc.LastProbeUtc = now;

                var threshold = Math.Max(loc.MinFreeGb * OneGb, 3 * estimatedBytes);

                // Apply failback hysteresis if checking a higher-priority location than active
                if (_activeLocation != null && loc != _activeLocation && _locations.IndexOf(loc) < _locations.IndexOf(_activeLocation))
                {
                    if (!_settings.FailbackToPrimary)
                    {
                        continue;
                    }
                    threshold += FailbackHysteresisBytes;
                }

                if (free > threshold)
                {
                    loc.State = StorageLocationState.Healthy;
                    _emergencyMode = false;

                    if (_activeLocation != null && _activeLocation != loc)
                    {
                        Log.Information("Storage location changing from {Old} to {New}", _activeLocation.ExpandedPath, loc.ExpandedPath);
                        SwitchRequested?.Invoke(this, new StorageSwitchRequestedEventArgs
                        {
                            NewLocation = loc.ExpandedPath,
                            Reason = "Higher priority location available or primary space low"
                        });
                    }

                    _activeLocation = loc;
                    return loc.ExpandedPath;
                }
                else
                {
                    loc.State = StorageLocationState.Low;
                }
            }

            // Emergency mode: all locations are low
            return HandleEmergencyMode(estimatedBytes);
        }
    }

    private string HandleEmergencyMode(long estimatedBytes)
    {
        _emergencyMode = true;
        Log.Warning("StorageManager: All storage locations are low on space! Entering Emergency Mode.");

        var candidates = _locations
            .Where(l => l.Enabled && l.State != StorageLocationState.Failed && _probe.IsReady(l.ExpandedPath))
            .OrderByDescending(l => l.AvailableFreeBytes)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new IOException("No accessible storage locations available for recording.");
        }

        var best = candidates[0];
        var isSystemDrive = string.Equals(Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'),
            Path.GetPathRoot(best.ExpandedPath)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        var hardFloor = isSystemDrive ? HardFloorSystemDriveBytes : HardFloorDataDriveBytes;
        if (best.AvailableFreeBytes < hardFloor)
        {
            throw new IOException($"Storage hard floor reached ({best.AvailableFreeBytes / OneGb:F1} GB free on {best.ExpandedPath}). Recording stopped to protect OS.");
        }

        _activeLocation = best;
        return best.ExpandedPath;
    }

    public void ReportWriteFailure(string locationPath, Exception exception)
    {
        lock (_lock)
        {
            var match = _locations.FirstOrDefault(l =>
                string.Equals(l.Path, locationPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(l.ExpandedPath, locationPath, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                Log.Warning(exception, "Marking storage location {Path} as Failed for 60s.", locationPath);
                match.State = StorageLocationState.Failed;
                match.RetryAtUtc = DateTime.UtcNow.AddSeconds(60);
            }
        }
    }

    public StorageStatus GetStatus()
    {
        lock (_lock)
        {
            return new StorageStatus(
                _locations.ToList(),
                _activeLocation?.ExpandedPath ?? string.Empty,
                _emergencyMode);
        }
    }

    private void ProbeAllLocations()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            var now = DateTime.UtcNow;

            foreach (var loc in _locations)
            {
                if (!loc.Enabled) continue;

                if (loc.State == StorageLocationState.Failed && loc.RetryAtUtc.HasValue && now < loc.RetryAtUtc.Value)
                {
                    continue;
                }

                if (_probe.IsReady(loc.ExpandedPath))
                {
                    loc.AvailableFreeBytes = _probe.GetAvailableFreeSpace(loc.ExpandedPath);
                    loc.LastProbeUtc = now;
                    var threshold = loc.MinFreeGb * OneGb;
                    if (loc.AvailableFreeBytes > threshold && loc.State != StorageLocationState.Failed)
                    {
                        loc.State = StorageLocationState.Healthy;
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _probeTimer.Dispose();
        }
    }
}
