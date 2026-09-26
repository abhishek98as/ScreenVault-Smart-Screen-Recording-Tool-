using System.IO.Abstractions;
using ScreenVault.Core.Infrastructure;
using Serilog;

namespace ScreenVault.Core.Sessions;

public interface IMarkerService
{
    void SetActiveSession(SessionManifest manifest, string? metadataBackupDir = null);
    void ClearActiveSession();
    MarkerEntry AddMarker(string note, string kind = "User");
    void AddEvent(string type, string detail);
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

    public event EventHandler<MarkerEntry>? MarkerAdded;

    public MarkerService(ISessionStore sessionStore, IClock? clock = null, IFileSystem? fileSystem = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _clock = clock ?? new SystemClock();
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public void SetActiveSession(SessionManifest manifest, string? metadataBackupDir = null)
    {
        lock (_lock)
        {
            _activeManifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _metadataBackupDir = metadataBackupDir;
        }
    }

    public void ClearActiveSession()
    {
        lock (_lock)
        {
            _activeManifest = null;
            _metadataBackupDir = null;
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
            var offsetSec = _activeManifest != null
                ? Math.Max(0.0, (nowUtc - _activeManifest.StartedAtUtc).TotalSeconds)
                : 0.0;

            marker = new MarkerEntry
            {
                AtUtc = nowUtc,
                OffsetSec = Math.Round(offsetSec, 2),
                Note = note,
                Kind = kind
            };

            if (_activeManifest != null)
            {
                _activeManifest.Markers.Add(marker);
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

            _activeManifest.Events.Add(entry);
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
