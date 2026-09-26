using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace ScreenVault.Core.Recording;

public sealed class ResumeState
{
    [JsonPropertyName("wasRecording")]
    public bool WasRecording { get; set; }

    [JsonPropertyName("atUtc")]
    public DateTimeOffset AtUtc { get; set; }
}

public static class ResumeStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string GetResumeFilePath(IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return fs.Path.Combine(localAppData, "ScreenVault", "resume.json");
    }

    public static void SaveResumeState(bool wasRecording, IFileSystem? fileSystem = null, DateTimeOffset? utcNow = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var path = GetResumeFilePath(fs);

        try
        {
            var dir = fs.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !fs.Directory.Exists(dir))
            {
                fs.Directory.CreateDirectory(dir);
            }

            var state = new ResumeState
            {
                WasRecording = wasRecording,
                AtUtc = utcNow ?? DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(state, JsonOptions);
            fs.File.WriteAllText(path, json);
            Log.Information("Saved resume state to {Path} (WasRecording={WasRecording}, AtUtc={AtUtc})", path, wasRecording, state.AtUtc);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save resume state to {Path}", path);
        }
    }

    public static ResumeState? TryGetValidResumeState(TimeSpan maxAge, IFileSystem? fileSystem = null, DateTimeOffset? utcNow = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var path = GetResumeFilePath(fs);

        if (!fs.File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = fs.File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<ResumeState>(json, JsonOptions);
            if (state == null)
            {
                return null;
            }

            var now = utcNow ?? DateTimeOffset.UtcNow;
            var age = now - state.AtUtc;

            if (state.WasRecording && age >= TimeSpan.Zero && age <= maxAge)
            {
                Log.Information("Found valid resume state at {Path} (age: {AgeSeconds:F1}s)", path, age.TotalSeconds);
                return state;
            }

            Log.Information("Resume state at {Path} expired or was not recording (age: {AgeSeconds:F1}s, wasRecording: {WasRecording})",
                path, age.TotalSeconds, state.WasRecording);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read or parse resume state at {Path}", path);
            return null;
        }
    }

    public static void ClearResumeState(IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var path = GetResumeFilePath(fs);

        try
        {
            if (fs.File.Exists(path))
            {
                fs.File.Delete(path);
                Log.Information("Deleted resume state at {Path}", path);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to delete resume state at {Path}", path);
        }
    }
}
