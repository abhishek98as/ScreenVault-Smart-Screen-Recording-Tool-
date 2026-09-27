using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.Storage;

public sealed class StorageSwitchRequestedEventArgs : EventArgs
{
    public string NewLocation { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// True when the current part should be closed now so that the next one is written to a better
    /// location. False when the switch already happened (a new part was just opened there).
    /// </summary>
    public bool RequiresRotation { get; init; }
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

    /// <summary>Applies edited storage settings (new folders, order, thresholds) without a restart.</summary>
    void UpdateSettings(StorageSettings settings)
    {
    }
}

public sealed class StorageManager : IStorageManager, IDisposable
{
    private const long OneGb = 1024L * 1024L * 1024L;
    private const long FailbackHysteresisBytes = 10L * OneGb;
    private const long HardFloorDataDriveBytes = 1L * OneGb;
    private const long HardFloorSystemDriveBytes = 5L * OneGb;

    private readonly IDiskSpaceProbe _probe;
    private readonly List<StorageLocationInfo> _locations = [];
    private readonly object _lock = new();
    private readonly System.Threading.Timer _probeTimer;

    private StorageSettings _settings;
    private StorageLocationInfo? _activeLocation;
    private bool _emergencyMode;
    private bool _lowSpaceSwitchRequested;
    private bool _isDisposed;

    public event EventHandler<StorageSwitchRequestedEventArgs>? SwitchRequested;

    public StorageManager(StorageSettings settings, IDiskSpaceProbe? probe = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _probe = probe ?? new SystemDiskSpaceProbe();

        _locations.AddRange(BuildLocations(_settings));

        ProbeAllLocations();
        _probeTimer = new System.Threading.Timer(_ => ProbeAllLocations(), null, 10000, 10000);
    }

    public void UpdateSettings(StorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            if (_isDisposed) return;

            var previous = _locations.ToDictionary(l => l.ExpandedPath, StringComparer.OrdinalIgnoreCase);
            var activePath = _activeLocation?.ExpandedPath;

            _settings = settings;
            _locations.Clear();
            foreach (var location in BuildLocations(settings))
            {
                // Keep what we already know about a location that is still configured.
                if (previous.TryGetValue(location.ExpandedPath, out var old) && location.State != StorageLocationState.Failed)
                {
                    location.State = old.State;
                    location.RetryAtUtc = old.RetryAtUtc;
                    location.AvailableFreeBytes = old.AvailableFreeBytes;
                    location.LastProbeUtc = old.LastProbeUtc;
                }

                _locations.Add(location);
            }

            _activeLocation = activePath == null
                ? null
                : _locations.FirstOrDefault(l => string.Equals(l.ExpandedPath, activePath, StringComparison.OrdinalIgnoreCase));
            _lowSpaceSwitchRequested = false;
            Log.Information("Storage locations updated: {Locations}", string.Join(", ", _locations.Select(l => l.ExpandedPath)));
        }

        ProbeAllLocations();
    }

    public string SelectLocationForNewSegment(long estimatedBytes)
    {
        StorageSwitchRequestedEventArgs? switchEvent = null;
        string selected;

        lock (_lock)
        {
            _lowSpaceSwitchRequested = false;
            selected = SelectLocationLocked(estimatedBytes, ref switchEvent);
        }

        // Raised outside the lock: handlers write to the session manifest.
        if (switchEvent != null)
        {
            SwitchRequested?.Invoke(this, switchEvent);
        }

        return selected;
    }

    private string SelectLocationLocked(long estimatedBytes, ref StorageSwitchRequestedEventArgs? switchEvent)
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
                    switchEvent = new StorageSwitchRequestedEventArgs
                    {
                        NewLocation = loc.ExpandedPath,
                        Reason = _locations.IndexOf(loc) < _locations.IndexOf(_activeLocation)
                            ? "Higher priority location available again"
                            : "Previous location is low on space or unavailable",
                        RequiresRotation = false
                    };
                }

                _activeLocation = loc;
                return loc.ExpandedPath;
            }

            loc.State = StorageLocationState.Low;
        }

        // Emergency mode: all locations are low
        return HandleEmergencyMode();
    }

    private string HandleEmergencyMode()
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
            throw new IOException($"Storage hard floor reached ({best.AvailableFreeBytes / (double)OneGb:F1} GB free on {best.ExpandedPath}). Recording stopped to protect OS.");
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
        StorageSwitchRequestedEventArgs? switchEvent = null;

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
                    else if (loc.AvailableFreeBytes <= threshold)
                    {
                        loc.State = StorageLocationState.Low;
                    }
                }
            }

            // The drive we are writing to is filling up: move to a healthy location at the next
            // keyframe instead of waiting for the current part to end (or for the disk to be full).
            var active = _activeLocation;
            if (active != null && active.State == StorageLocationState.Low && !_lowSpaceSwitchRequested)
            {
                var alternative = _locations.FirstOrDefault(l =>
                    l != active &&
                    l.Enabled &&
                    l.State == StorageLocationState.Healthy &&
                    l.AvailableFreeBytes > l.MinFreeGb * OneGb);
                if (alternative != null)
                {
                    _lowSpaceSwitchRequested = true;
                    Log.Warning("Storage location {Active} is low on space; switching to {Alternative} at the next part.",
                        active.ExpandedPath, alternative.ExpandedPath);
                    switchEvent = new StorageSwitchRequestedEventArgs
                    {
                        NewLocation = alternative.ExpandedPath,
                        Reason = $"{active.ExpandedPath} is low on space",
                        RequiresRotation = true
                    };
                }
            }
        }

        if (switchEvent != null)
        {
            try
            {
                SwitchRequested?.Invoke(this, switchEvent);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling a storage switch request.");
            }
        }
    }

    private static List<StorageLocationInfo> BuildLocations(StorageSettings settings)
    {
        var result = new List<StorageLocationInfo>();
        foreach (var locConfig in settings.Locations)
        {
            var expanded = Environment.ExpandEnvironmentVariables(locConfig.Path);
            if (!Path.IsPathRooted(expanded))
            {
                Log.Error("Storage location path '{Path}' expanded to '{Expanded}' is not a rooted absolute path. Rejecting location.", locConfig.Path, expanded);
                result.Add(new StorageLocationInfo(locConfig.Path, locConfig.MinFreeGb, false)
                {
                    State = StorageLocationState.Failed
                });
                continue;
            }

            result.Add(new StorageLocationInfo(locConfig.Path, locConfig.MinFreeGb, locConfig.Enabled));
        }

        return result;
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
