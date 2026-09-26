using System.IO.Abstractions;
using Serilog;

namespace ScreenVault.Core.Settings;

public static class SettingsMigrator
{
    public const int CurrentSchemaVersion = 2;

    public static AppSettings Migrate(AppSettings settings, string? settingsPath = null, IFileSystem? fileSystem = null)
    {
        if (settings.SchemaVersion >= CurrentSchemaVersion)
        {
            return settings;
        }

        Log.Information("Migrating settings from schema v{OldVersion} to v{NewVersion}",
            settings.SchemaVersion, CurrentSchemaVersion);

        // v1 -> v2 migration
        if (settings.SchemaVersion < 2)
        {
            // 1. Backup old settings.json to settings.v1.bak if fileSystem and path available
            if (!string.IsNullOrEmpty(settingsPath) && fileSystem != null && fileSystem.File.Exists(settingsPath))
            {
                try
                {
                    var backupPath = fileSystem.Path.ChangeExtension(settingsPath, ".v1.bak");
                    fileSystem.File.Copy(settingsPath, backupPath, overwrite: true);
                    Log.Information("Created v1 settings backup at {BackupPath}", backupPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not create v1 settings backup file.");
                }
            }

            // 2. Set manual start by default
            settings.General.StartRecordingOnLaunch = false;

            // 3. Migrate default frame rate to 15 fps (if at old v1 default of 30)
            if (settings.Video.FrameRate == 30)
            {
                Log.Information("Migrated video frame rate from 30 to 15 fps for stability.");
                settings.Video.FrameRate = 15;
            }

            // 4. Ensure all new v2 sections exist with defaults
            settings.Audio ??= new AudioSettings();
            settings.Saving ??= new SavingSettings();
            settings.Playback ??= new PlaybackSettings();
            settings.MeetingDetection ??= new MeetingDetectionSettings();
            settings.Reminders ??= new ReminderSettings();
            settings.Transcription ??= new TranscriptionSettings();
            settings.Hotkeys ??= new HotkeySettings();
            settings.Advanced ??= new AdvancedSettings();

            settings.SchemaVersion = 2;
        }

        return settings;
    }
}
