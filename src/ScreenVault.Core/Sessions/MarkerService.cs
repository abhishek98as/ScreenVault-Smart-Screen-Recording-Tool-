using System.IO.Abstractions;
using ScreenVault.Core.Infrastructure;
using Serilog;

namespace ScreenVault.Core.Sessions;

public interface IMarkerService
{
    /// <param name="recordedSecondsProvider">
    /// Returns the recorded time so far (paused time excluded). Marker offsets use it so they line
    /// up with the video; without it the wall-clock time since the session started is used.
    /// </param>
    void SetActiveSession(SessionManifest manifest, string? metadataBackupDir = null, Func<double>? recordedSecondsProvider = null);
    void ClearActiveSession();
    MarkerEntry AddMarker(string note, string kind = "User");
    void AddEvent(string type, string detail);
    bool HasActiveSession => false;
    event EventHandler<MarkerEntry>? MarkerAdded;
}

public sealed class MarkerService : IMarkerService
{
    private readonly ISessionStore _sessionStore;
    private readonly IClock _clock;
    private readonly IFileSystem _fileSystem;
    private readonly object _lock = new();

    private SessionManifest? _activeManifest;
    private string? _metadataBackupDir;
    private Func<double>? _recordedSecondsProvider;

    public event EventHandler<MarkerEntry>? MarkerAdded;

    public MarkerService(ISessionStore sessionStore, IClock? clock = null, IFileSystem? fileSystem = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _clock = clock ?? new SystemClock();
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public bool HasActiveSession
    {
        get
        {
            lock (_lock)
            {
                return _activeManifest != null;
            }
        }
    }

    public void SetActiveSession(SessionManifest manifest, string? metadataBackupDir = null, Func<double>? recordedSecondsProvider = null)
    {
        lock (_lock)
        {
            _activeManifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _metadataBackupDir = metadataBackupDir;
            _recordedSecondsProvider = recordedSecondsProvider;
        }
    }

    public void ClearActiveSession()
    {
        lock (_lock)
        {
            _activeManifest = null;
            _metadataBackupDir = null;
            _recordedSecondsProvider = null;
        }
    }

    public MarkerEntry AddMarker(string note, string kind = "User")
    {
        ArgumentNullException.ThrowIfNull(note);

        MarkerEntry marker;
        string? metadataBackupDir;
        SessionManifest? manifest;

        lock (_lock)
        {
            var nowUtc = _clock.UtcNow;
            var offsetSec = 0.0;
            if (_activeManifest != null)
            {
                offsetSec = _recordedSecondsProvider != null
                    ? _recordedSecondsProvider()
                    : (nowUtc - _activeManifest.StartedAtUtc).TotalSeconds;
            }

            marker = new MarkerEntry
            {
                AtUtc = nowUtc,
                OffsetSec = Math.Round(Math.Max(0.0, offsetSec), 2),
                Note = note,
                Kind = kind
            };

            if (_activeManifest != null)
            {
                lock (_activeManifest)
                {
                    _activeManifest.Markers.Add(marker);
                }
            }

            manifest = _activeManifest;
            metadataBackupDir = _metadataBackupDir;
        }

        if (manifest != null)
        {
            try
            {
                var mirrors = !string.IsNullOrEmpty(metadataBackupDir) ? new[] { metadataBackupDir } : null;
                _sessionStore.Save(manifest, mirrors);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to persist marker to session manifest.");
            }
        }
        else
        {
            Log.Information("Marker '{Note}' ignored: no recording is active.", note);
            return marker;
        }

        Log.Information("Marker added: [{Kind}] {Note} (offset {OffsetSec}s)", kind, note, marker.OffsetSec);
        MarkerAdded?.Invoke(this, marker);
        return marker;
    }

    public void AddEvent(string type, string detail)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(detail);

        SessionManifest? manifest;
        string? metadataBackupDir;

        lock (_lock)
        {
            if (_activeManifest == null)
            {
                return;
            }

            var entry = new SessionEventEntry
            {
                AtUtc = _clock.UtcNow,
                Type = type,
                Detail = detail
            };

            lock (_activeManifest)
            {
                _activeManifest.Events.Add(entry);
            }

            manifest = _activeManifest;
            metadataBackupDir = _metadataBackupDir;
        }

        try
        {
            var mirrors = !string.IsNullOrEmpty(metadataBackupDir) ? new[] { metadataBackupDir } : null;
            _sessionStore.Save(manifest, mirrors);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to persist event to session manifest.");
        }
    }
}
