using System.Text.Json.Serialization;

namespace ScreenVault.Core.Settings;

public sealed class InstallDefaults
{
    [JsonPropertyName("installScope")]
    public string? InstallScope { get; set; }

    [JsonPropertyName("installerVersion")]
    public string? InstallerVersion { get; set; }

    [JsonPropertyName("defaultsRevision")]
    public string? DefaultsRevision { get; set; }

    [JsonPropertyName("primaryLocation")]
    public string? PrimaryLocation { get; set; }

    [JsonPropertyName("backupLocation")]
    public string? BackupLocation { get; set; }

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = true;

    [JsonPropertyName("startRecordingOnLaunch")]
    public bool StartRecordingOnLaunch { get; set; }
}
