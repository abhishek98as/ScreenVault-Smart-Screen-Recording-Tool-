using System.Text.Json.Serialization;

namespace ScreenVault.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MicMode
{
    DefaultCommunications,
    DefaultMultimedia,
    Specific,
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputMode
{
    Default,
    DefaultPlusCommunications,
    AllActive,
    Specific,
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoQuality
{
    Small,
    Balanced,
    High
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputContainerFormat
{
    Mkv,
    Mp4,
    Ts
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaybackPlayer
{
    Auto,
    Vlc,
    Ffplay,
    SystemDefault,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeetingDetectionMode
{
    Off,
    Ask,
    AutoStart
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranscriptionMode
{
    Off,
    WhenIdle,
    AfterEachPart
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool StartRecordingOnLaunch { get; set; }
    public bool MinimizeToTrayOnLaunch { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 5;
    public bool ConfirmBeforeStop { get; set; } = true;
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public NotificationSettings Notifications { get; set; } = new();
}

public sealed class NotificationSettings
{
    public bool DeviceSwitch { get; set; } = true;
    public bool Storage { get; set; } = true;
}

public sealed class VideoSettings
{
    public int MonitorIndex { get; set; }
    public int FrameRate { get; set; } = 15;
    public VideoQuality Quality { get; set; } = VideoQuality.Balanced;
    public string Encoder { get; set; } = "Auto";
    public string? DetectedEncoderProfile { get; set; }
    public string? EncoderFingerprint { get; set; }
    public bool CaptureCursor { get; set; } = true;
    public bool DownscaleTo1080p { get; set; }
}

public sealed class AudioSettings
{
    public MicMode MicMode { get; set; } = MicMode.DefaultCommunications;
    public string? MicDeviceId { get; set; }
    public OutputMode OutputMode { get; set; } = OutputMode.DefaultPlusCommunications;
    public string? OutputDeviceId { get; set; }
    public float MicGainDb { get; set; }
    public float SystemGainDb { get; set; }
    public bool SeparateTracks { get; set; }
    public int JitterTargetMs { get; set; } = 100;
    public int AvOffsetMs { get; set; }
    public bool SilentKeepAlive { get; set; }
    public bool UnmuteOnNewSession { get; set; } = true;
}

public sealed class StorageLocationConfig
{
    public string Path { get; set; } = string.Empty;
    public int MinFreeGb { get; set; } = 5;
    public bool Enabled { get; set; } = true;
}

public sealed class RetentionSettings
{
    public bool Enabled { get; set; }
    public int KeepDays { get; set; } = 30;
    public bool ProtectSessionsWithMarkers { get; set; } = true;
}

public sealed class StorageSettings
{
    public List<StorageLocationConfig> Locations { get; set; } = new();
    public int SplitMinutes { get; set; } = 10;
    public int SplitSizeMb { get; set; }
    public OutputContainerFormat OutputFormat { get; set; } = OutputContainerFormat.Mkv;
    public bool KeepTsAfterRemux { get; set; }
    public bool FailbackToPrimary { get; set; } = true;
    public bool MetadataNextToRecordings { get; set; } = true;
    public RetentionSettings Retention { get; set; } = new();
}

public sealed class SavingSettings
{
    public bool MergeOnSave { get; set; } = true;
    public bool DeletePartsAfterMerge { get; set; }
    public bool ShowSavedDialog { get; set; } = true;
}

public sealed class PlaybackSettings
{
    public PlaybackPlayer Player { get; set; } = PlaybackPlayer.Auto;
    public string? CustomPlayerPath { get; set; }
    public int MarkerPreRollSec { get; set; } = 5;
}

public sealed class MeetingDetectionSettings
{
    public MeetingDetectionMode Mode { get; set; } = MeetingDetectionMode.Ask;
    public List<string> WatchedApps { get; set; } = ["MSTeams", "ms-teams.exe", "Teams.exe", "Zoom.exe", "chrome.exe", "msedge.exe", "firefox.exe", "slack.exe", "Discord.exe"];
    public bool AnyApp { get; set; }
    public List<string> AutoStartApps { get; set; } = new();
    public bool PromptStopWhenMeetingEnds { get; set; } = true;
}

public sealed class ReminderSettings
{
    public bool Enabled { get; set; }
    public List<string> WorkDays { get; set; } = ["Mon", "Tue", "Wed", "Thu", "Fri"];
    public string WorkStart { get; set; } = "09:00";
    public string WorkEnd { get; set; } = "18:00";
    public int RemindAfterMinutes { get; set; } = 15;
    public int RepeatEveryMinutes { get; set; } = 30;
}

public sealed class HotkeySettings
{
    public string StartStop { get; set; } = "Ctrl+Alt+Shift+R";
    public string MuteMic { get; set; } = "Ctrl+Alt+Shift+X";
    public string AddMarker { get; set; } = "Ctrl+Alt+Shift+M";
    public string PauseResume { get; set; } = "Ctrl+Alt+Shift+P";
    public string ShowStatus { get; set; } = "Ctrl+Alt+Shift+S";
}

public sealed class TranscriptionSettings
{
    public TranscriptionMode Mode { get; set; } = TranscriptionMode.Off;
    public string? WhisperCliPath { get; set; }
    public string? ModelPath { get; set; }
    public string Language { get; set; } = "en";
}

public sealed class AdvancedSettings
{
    public string? FfmpegPath { get; set; }
    public string LogLevel { get; set; } = "Information";
    public int FfmpegMemoryWarnMb { get; set; } = 400;
    public int FfmpegMemoryRestartMb { get; set; } = 600;
    public double SpeedDegradeThreshold { get; set; } = 0.95;
    public int SpeedDegradeSeconds { get; set; } = 20;
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public string? AppliedDefaultsRevision { get; set; }
    public GeneralSettings General { get; set; } = new();
    public VideoSettings Video { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public SavingSettings Saving { get; set; } = new();
    public PlaybackSettings Playback { get; set; } = new();
    public MeetingDetectionSettings MeetingDetection { get; set; } = new();
    public ReminderSettings Reminders { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public TranscriptionSettings Transcription { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();

    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings();
        settings.Storage.Locations.Add(new StorageLocationConfig
        {
            Path = @"%USERPROFILE%\Videos\Screen Recordings",
            MinFreeGb = 5,
            Enabled = true
        });

        // Detect if another fixed drive exists for backup location
        try
        {
            var systemDrive = System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
            var otherDrive = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.Name.TrimEnd('\\'), systemDrive, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.TotalSize)
                .FirstOrDefault();

            if (otherDrive != null)
            {
                settings.Storage.Locations.Add(new StorageLocationConfig
                {
                    Path = System.IO.Path.Combine(otherDrive.Name, "ScreenVault Backup"),
                    MinFreeGb = 5,
                    Enabled = true
                });
            }
        }
        catch
        {
            // Fallback default if drive query fails
            settings.Storage.Locations.Add(new StorageLocationConfig
            {
                Path = @"D:\ScreenVault Backup",
                MinFreeGb = 5,
                Enabled = true
            });
        }

        return settings;
    }

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefault();
    }
}
