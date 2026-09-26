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

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var canonicalPath = GetCanonicalPath(manifest.SessionId);

        lock (_lock)
        {
            try
            {
                _atomicFile.WriteAllText(canonicalPath, json);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save canonical session manifest to {Path}", canonicalPath);
                throw;
            }

            if (mirrorDirectories != null)
            {
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
        }
    }

    public void Delete(string sessionId, IEnumerable<string>? mirrorDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cleanId = sessionId;
        if (cleanId.StartsWith("SV_", StringComparison.OrdinalIgnoreCase)) cleanId = cleanId[3..];
        if (cleanId.EndsWith(".session.json", StringComparison.OrdinalIgnoreCase)) cleanId = cleanId[..^13];
        else if (cleanId.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) cleanId = cleanId[..^5];

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
        var path = GetCanonicalPath(sessionId);
        var manifest = LoadFromPath(path);
        if (manifest != null) return manifest;

        var cleanId = sessionId;
        if (cleanId.StartsWith("SV_", StringComparison.OrdinalIgnoreCase)) cleanId = cleanId[3..];
        if (cleanId.EndsWith(".session.json", StringComparison.OrdinalIgnoreCase)) cleanId = cleanId[..^13];
        else if (cleanId.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) cleanId = cleanId[..^5];

        string[] candidates =
        [
            _fileSystem.Path.Combine(_canonicalDir, $"SV_{cleanId}.json"),
            _fileSystem.Path.Combine(_canonicalDir, $"{cleanId}.json"),
            _fileSystem.Path.Combine(_canonicalDir, $"SV_{cleanId}.session.json"),
            _fileSystem.Path.Combine(_canonicalDir, $"{cleanId}.session.json")
        ];

        foreach (var c in candidates)
        {
            manifest = LoadFromPath(c);
            if (manifest != null) return manifest;
        }

        return null;
    }

    public SessionManifest? LoadFromPath(string manifestPath)
    {
        lock (_lock)
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
                var manifest = LoadFromPath(file);
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
}
