using System.IO.Abstractions;
using System.Text.Json;
using Serilog;

namespace ScreenVault.Core.Settings;

public static class InstallDefaultsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string GetDefaultPrimaryLocation()
    {
        var myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(myVideos))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            myVideos = Path.Combine(userProfile, "Videos");
        }
        return Path.Combine(myVideos, "Screen Recordings");
    }

    public static InstallDefaults? LoadDefaults(string filePath, IFileSystem fileSystem)
    {
        if (!fileSystem.File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = fileSystem.File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<InstallDefaults>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read or parse install-defaults.json at {Path}", filePath);
            return null;
        }
    }

    public static bool TryApplyDefaults(
        ISettingsService settingsService,
        string? defaultsFilePath = null,
        IFileSystem? fileSystem = null,
        Action<string>? onDirectoriesCreated = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var path = defaultsFilePath ?? fs.Path.Combine(AppContext.BaseDirectory, "install-defaults.json");

        var defaults = LoadDefaults(path, fs);
        if (defaults == null || string.IsNullOrWhiteSpace(defaults.DefaultsRevision))
        {
            return false;
        }

        var currentSettings = settingsService.Current;
        if (string.Equals(currentSettings.AppliedDefaultsRevision, defaults.DefaultsRevision, StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug("Install defaults revision {Revision} already applied.", defaults.DefaultsRevision);
            return false;
        }

        Log.Information("Applying install-defaults revision {Revision} (Scope={Scope}, Version={Version})",
            defaults.DefaultsRevision, defaults.InstallScope, defaults.InstallerVersion);

        var updatedSettings = currentSettings.Clone();

        // 1. Resolve Primary Location
        var primaryPath = defaults.PrimaryLocation;
        if (string.IsNullOrWhiteSpace(primaryPath))
        {
            primaryPath = GetDefaultPrimaryLocation();
        }
        else
        {
            primaryPath = Environment.ExpandEnvironmentVariables(primaryPath);
        }

        // 2. Storage locations update
        var newLocations = new List<StorageLocationConfig>
        {
            new()
            {
                Path = primaryPath,
                MinFreeGb = 5,
                Enabled = true
            }
        };

        if (!string.IsNullOrWhiteSpace(defaults.BackupLocation))
        {
            var backupPath = Environment.ExpandEnvironmentVariables(defaults.BackupLocation);
            newLocations.Add(new StorageLocationConfig
            {
                Path = backupPath,
                MinFreeGb = 5,
                Enabled = true
            });
        }

        updatedSettings.Storage.Locations = newLocations;

        // 3. Startup options
        updatedSettings.General.StartWithWindows = defaults.StartWithWindows;
        updatedSettings.General.StartRecordingOnLaunch = defaults.StartRecordingOnLaunch;

        // 4. Record applied revision
        updatedSettings.AppliedDefaultsRevision = defaults.DefaultsRevision;

        // 5. Save settings
        settingsService.Save(updatedSettings);

        // 6. Create directories as current user
        foreach (var loc in newLocations)
        {
            if (string.IsNullOrWhiteSpace(loc.Path)) continue;
            try
            {
                if (!fs.Directory.Exists(loc.Path))
                {
                    fs.Directory.CreateDirectory(loc.Path);
                    Log.Information("Created storage directory: {Dir}", loc.Path);
                    onDirectoriesCreated?.Invoke(loc.Path);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not create storage directory: {Dir}", loc.Path);
            }
        }

        return true;
    }
}
