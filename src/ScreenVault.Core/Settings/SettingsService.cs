using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenVault.Core.Infrastructure;
using Serilog;

namespace ScreenVault.Core.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsFilePath { get; }
    bool WasMigrated { get; }

    /// <summary>True when no settings existed yet (first launch for this user).</summary>
    bool IsFirstRun => false;

    event EventHandler<AppSettings>? SettingsChanged;
    void Save(AppSettings newSettings);
    void Reload();
}

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private readonly IFileSystem _fileSystem;
    private readonly AtomicFile _atomicFile;
    private readonly string _settingsPath;
    private readonly string _backupPath;
    private readonly object _lock = new();
    private AppSettings _current;

    public event EventHandler<AppSettings>? SettingsChanged;

    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public string SettingsFilePath => _settingsPath;
    public bool WasMigrated { get; private set; }
    public bool IsFirstRun { get; private set; }

    public SettingsService(string? customSettingsPath = null, bool isPortable = false, IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
        _atomicFile = new AtomicFile(_fileSystem);

        if (!string.IsNullOrEmpty(customSettingsPath))
        {
            _settingsPath = customSettingsPath;
        }
        else if (isPortable)
        {
            _settingsPath = _fileSystem.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _settingsPath = _fileSystem.Path.Combine(appData, "ScreenVault", "settings.json");
        }

        _backupPath = _settingsPath + ".bak";
        IsFirstRun = !_fileSystem.File.Exists(_settingsPath) && !_fileSystem.File.Exists(_backupPath);
        _current = LoadInternal();
    }

    public void Reload()
    {
        lock (_lock)
        {
            _current = LoadInternal();
            SettingsChanged?.Invoke(this, _current);
        }
    }

    public void Save(AppSettings newSettings)
    {
        lock (_lock)
        {
            var migrated = SettingsMigrator.Migrate(newSettings, _settingsPath, _fileSystem);
            var validation = SettingsValidator.Validate(migrated);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"Invalid settings: {string.Join("; ", validation.Errors)}");
            }

            var json = JsonSerializer.Serialize(migrated, JsonOptions);
            _atomicFile.WriteAllText(_settingsPath, json, _backupPath);
            _current = migrated;
            Log.Information("Settings saved successfully to {Path}", _settingsPath);
            SettingsChanged?.Invoke(this, _current);
        }
    }

    private AppSettings LoadInternal()
    {
        if (_fileSystem.File.Exists(_settingsPath))
        {
            try
            {
                var content = _fileSystem.File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(content, JsonOptions);
                if (loaded != null)
                {
                    var oldVersion = loaded.SchemaVersion;
                    var migrated = SettingsMigrator.Migrate(loaded, _settingsPath, _fileSystem);
                    if (oldVersion < SettingsMigrator.CurrentSchemaVersion)
                    {
                        WasMigrated = true;
                        try
                        {
                            var json = JsonSerializer.Serialize(migrated, JsonOptions);
                            _atomicFile.WriteAllText(_settingsPath, json, _backupPath);
                            Log.Information("Settings migrated from schema v{Old} to v{New} and saved to disk at {Path}",
                                oldVersion, migrated.SchemaVersion, _settingsPath);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Could not persist migrated settings to {Path}", _settingsPath);
                        }
                    }
                    return migrated;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to parse primary settings file at {Path}. Attempting backup file.", _settingsPath);
            }
        }

        if (_fileSystem.File.Exists(_backupPath))
        {
            try
            {
                var content = _fileSystem.File.ReadAllText(_backupPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(content, JsonOptions);
                if (loaded != null)
                {
                    Log.Information("Loaded settings from backup file {BackupPath}", _backupPath);
                    return SettingsMigrator.Migrate(loaded);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to parse backup settings file at {BackupPath}", _backupPath);
            }
        }

        Log.Information("Settings file not found or corrupted. Generating default settings.");
        var defaults = AppSettings.CreateDefault();
        try
        {
            var json = JsonSerializer.Serialize(defaults, JsonOptions);
            _atomicFile.WriteAllText(_settingsPath, json, _backupPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not write initial default settings file to {Path}", _settingsPath);
        }

        return defaults;
    }
}
