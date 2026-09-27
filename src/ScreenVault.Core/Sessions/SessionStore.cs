using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenVault.Core.Infrastructure;
using Serilog;

namespace ScreenVault.Core.Sessions;

public interface ISessionStore
{
    void Save(SessionManifest manifest, IEnumerable<string>? mirrorDirectories = null);
    void Delete(string sessionId, IEnumerable<string>? mirrorDirectories = null);
    SessionManifest? Load(string sessionId);
    SessionManifest? LoadFromPath(string manifestPath);
    IReadOnlyList<SessionManifest> LoadAllCanonical();
    string GetCanonicalPath(string sessionId);

    /// <summary>
    /// Applies <paramref name="mutate"/> to the latest version of a session and saves it, as one step,
    /// so concurrent edits (remux results, renames, merges) never overwrite each other.
    /// <paramref name="mutate"/> returns false when it changed nothing. Returns true when saved.
    /// </summary>
    bool Update(string sessionId, Func<SessionManifest, bool> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var manifest = Load(sessionId);
        if (manifest == null || !mutate(manifest))
        {
            return false;
        }

        Save(manifest);
        return true;
    }

    /// <summary>
    /// Marks a session as being recorded: its in-memory manifest is the source of truth, so
    /// <see cref="Update"/> applies changes to that instance instead of to the file on disk.
    /// Code that changes a live manifest must lock on it while doing so.
    /// </summary>
    void RegisterLive(SessionManifest manifest, IEnumerable<string>? mirrorDirectories = null)
    {
    }

    void UnregisterLive(string sessionId)
    {
    }

    bool IsLive(string sessionId) => false;
}

public sealed class SessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IFileSystem _fileSystem;
    private readonly AtomicFile _atomicFile;
    private readonly string _canonicalDir;
    private readonly object _lock = new();
    private readonly Dictionary<string, LiveSession> _live = new(StringComparer.OrdinalIgnoreCase);

    public SessionStore(IFileSystem? fileSystem = null, string? canonicalDir = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
        _atomicFile = new AtomicFile(_fileSystem);
        _canonicalDir = canonicalDir ?? _fileSystem.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenVault", "sessions");

        if (!_fileSystem.Directory.Exists(_canonicalDir))
        {
            _fileSystem.Directory.CreateDirectory(_canonicalDir);
        }
    }

    public string GetCanonicalPath(string sessionId)
    {
        return _fileSystem.Path.Combine(_canonicalDir, $"SV_{sessionId}.session.json");
    }

    public void Save(SessionManifest manifest, IEnumerable<string>? mirrorDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        lock (_lock)
        {
            SaveCore(manifest, mirrorDirectories);
        }
    }

    public bool Update(string sessionId, Func<SessionManifest, bool> mutate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_lock)
        {
            if (_live.TryGetValue(sessionId, out var live))
            {
                bool changed;
                lock (live.Manifest)
                {
                    changed = mutate(live.Manifest);
                }

                if (changed)
                {
                    SaveCore(live.Manifest, live.MirrorDirectories);
                }

                return changed;
            }

            var manifest = LoadFromPathCore(GetCanonicalPath(sessionId)) ?? LoadLegacyCore(sessionId);
            if (manifest == null || !mutate(manifest))
            {
                return false;
            }

            SaveCore(manifest, ExistingMirrorDirectories(manifest));
            return true;
        }
    }

    public void RegisterLive(SessionManifest manifest, IEnumerable<string>? mirrorDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_lock)
        {
            _live[manifest.SessionId] = new LiveSession(manifest, mirrorDirectories?.Where(d => !string.IsNullOrWhiteSpace(d)).ToArray());
        }
    }

    public void UnregisterLive(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        lock (_lock)
        {
            _live.Remove(sessionId);
        }
    }

    public bool IsLive(string sessionId)
    {
        lock (_lock)
        {
            return _live.ContainsKey(sessionId);
        }
    }

    public void Delete(string sessionId, IEnumerable<string>? mirrorDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cleanId = CleanId(sessionId);
        string[] candidateNames =
        [
            $"SV_{cleanId}.session.json",
            $"SV_{cleanId}.json",
            $"{cleanId}.session.json",
            $"{cleanId}.json"
        ];

        lock (_lock)
        {
            foreach (var name in candidateNames)
            {
                var p = _fileSystem.Path.Combine(_canonicalDir, name);
                try
                {
                    if (_fileSystem.File.Exists(p))
                    {
                        _fileSystem.File.Delete(p);
                        Log.Information("Deleted canonical session manifest: {Path}", p);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete canonical session manifest {Path}", p);
                }
            }

            if (mirrorDirectories != null)
            {
                foreach (var dir in mirrorDirectories)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        var expandedDir = Environment.ExpandEnvironmentVariables(dir);
                        foreach (var name in candidateNames)
                        {
                            var mirrorPath = _fileSystem.Path.Combine(expandedDir, name);
                            if (_fileSystem.File.Exists(mirrorPath))
                            {
                                _fileSystem.File.Delete(mirrorPath);
                                Log.Information("Deleted mirror session manifest: {Path}", mirrorPath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to delete mirror session manifest in {Dir}", dir);
                    }
                }
            }
        }
    }

    public SessionManifest? Load(string sessionId)
    {
        lock (_lock)
        {
            return LoadFromPathCore(GetCanonicalPath(sessionId)) ?? LoadLegacyCore(sessionId);
        }
    }

    public SessionManifest? LoadFromPath(string manifestPath)
    {
        lock (_lock)
        {
            return LoadFromPathCore(manifestPath);
        }
    }

    public IReadOnlyList<SessionManifest> LoadAllCanonical()
    {
        lock (_lock)
        {
            var results = new Dictionary<string, SessionManifest>(StringComparer.OrdinalIgnoreCase);
            if (!_fileSystem.Directory.Exists(_canonicalDir))
            {
                return [];
            }

            var files = _fileSystem.Directory.GetFiles(_canonicalDir, "*.json");
            foreach (var file in files)
            {
                var manifest = LoadFromPathCore(file);
                if (manifest != null && !string.IsNullOrEmpty(manifest.SessionId))
                {
                    if (!results.ContainsKey(manifest.SessionId))
                    {
                        results[manifest.SessionId] = manifest;
                    }
                }
            }

            return results.Values.OrderByDescending(s => s.StartedAtUtc).ToList();
        }
    }

    private void SaveCore(SessionManifest manifest, IEnumerable<string>? mirrorDirectories)
    {
        // Serialize under the manifest's own lock: the recorder, marker service and post-processor
        // change live manifests from different threads.
        string json;
        lock (manifest)
        {
            json = JsonSerializer.Serialize(manifest, JsonOptions);
        }

        var canonicalPath = GetCanonicalPath(manifest.SessionId);
        try
        {
            _atomicFile.WriteAllText(canonicalPath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save canonical session manifest to {Path}", canonicalPath);
            throw;
        }

        if (mirrorDirectories == null)
        {
            return;
        }

        foreach (var dir in mirrorDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var expandedDir = Environment.ExpandEnvironmentVariables(dir);
                if (!Path.IsPathRooted(expandedDir))
                {
                    Log.Warning("SessionStore: Mirror directory '{Dir}' expanded to '{Expanded}' is not rooted. Skipping.", dir, expandedDir);
                    continue;
                }

                if (!_fileSystem.Directory.Exists(expandedDir))
                {
                    _fileSystem.Directory.CreateDirectory(expandedDir);
                }

                var mirrorPath = _fileSystem.Path.Combine(expandedDir, $"SV_{manifest.SessionId}.session.json");
                _atomicFile.WriteAllText(mirrorPath, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not mirror session manifest to {Dir}", dir);
            }
        }
    }

    /// <summary>Metadata folders next to the recordings that already hold a copy of this manifest.</summary>
    private List<string> ExistingMirrorDirectories(SessionManifest manifest)
    {
        var result = new List<string>();
        foreach (var location in manifest.Segments.Select(s => s.Location).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(location);
                if (!Path.IsPathRooted(expanded))
                {
                    continue;
                }

                var dir = _fileSystem.Path.Combine(_fileSystem.Path.GetFullPath(expanded), ".screenvault", "sessions");
                if (_fileSystem.File.Exists(_fileSystem.Path.Combine(dir, $"SV_{manifest.SessionId}.session.json")))
                {
                    result.Add(dir);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Could not check the metadata mirror in {Location}", location);
            }
        }

        return result;
    }

    private SessionManifest? LoadLegacyCore(string sessionId)
    {
        var cleanId = CleanId(sessionId);
        string[] candidates =
        [
            _fileSystem.Path.Combine(_canonicalDir, $"SV_{cleanId}.json"),
            _fileSystem.Path.Combine(_canonicalDir, $"{cleanId}.json"),
            _fileSystem.Path.Combine(_canonicalDir, $"SV_{cleanId}.session.json"),
            _fileSystem.Path.Combine(_canonicalDir, $"{cleanId}.session.json")
        ];

        foreach (var c in candidates)
        {
            var manifest = LoadFromPathCore(c);
            if (manifest != null) return manifest;
        }

        return null;
    }

    private SessionManifest? LoadFromPathCore(string manifestPath)
    {
        if (!_fileSystem.File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = _fileSystem.File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<SessionManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read session manifest at {Path}", manifestPath);
            return null;
        }
    }

    private static string CleanId(string sessionId)
    {
        var cleanId = sessionId;
        if (cleanId.StartsWith("SV_", StringComparison.OrdinalIgnoreCase)) cleanId = cleanId[3..];
        if (cleanId.EndsWith(".session.json", StringComparison.OrdinalIgnoreCase)) cleanId = cleanId[..^13];
        else if (cleanId.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) cleanId = cleanId[..^5];
        return cleanId;
    }

    private sealed record LiveSession(SessionManifest Manifest, string[]? MirrorDirectories);
}
