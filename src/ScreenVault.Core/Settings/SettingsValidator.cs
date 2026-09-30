namespace ScreenVault.Core.Settings;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public static class SettingsValidator
{
    public static ValidationResult Validate(AppSettings settings)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (settings.General.StartupDelaySeconds < 0 || settings.General.StartupDelaySeconds > 120)
        {
            errors.Add("Startup delay must be between 0 and 120 seconds.");
        }

        if (settings.Video.FrameRate is < 5 or > 60)
        {
            errors.Add("Video frame rate must be between 5 and 60 fps.");
        }

        if (settings.Audio.JitterTargetMs is < 30 or > 1000)
        {
            errors.Add("Audio jitter target must be between 30 and 1000 ms.");
        }

        if (settings.Audio.MicGainDb is < -30 or > 30)
        {
            errors.Add("Microphone gain must be between -30 dB and +30 dB.");
        }

        if (settings.Audio.SystemGainDb is < -30 or > 30)
        {
            errors.Add("System gain must be between -30 dB and +30 dB.");
        }

        // 0 = don't split by time; otherwise 1-1440 minutes.
        if (settings.Storage.SplitMinutes != 0 && settings.Storage.SplitMinutes is < 1 or > 1440)
        {
            errors.Add("File split duration must be 0 (no split) or between 1 and 1440 minutes.");
        }

        var enabledLocations = settings.Storage.Locations.Where(l => l.Enabled).ToList();
        if (enabledLocations.Count == 0)
        {
            errors.Add("At least one storage location must be enabled.");
        }

        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in enabledLocations)
        {
            if (string.IsNullOrWhiteSpace(loc.Path))
            {
                errors.Add("Storage location path cannot be empty.");
                continue;
            }

            if (loc.MinFreeGb < 1)
            {
                errors.Add($"Storage location '{loc.Path}' min free space must be at least 1 GB.");
            }

            var expanded = Environment.ExpandEnvironmentVariables(loc.Path);
            try
            {
                var root = Path.GetPathRoot(expanded);
                if (!string.IsNullOrEmpty(root))
                    if (!seenRoots.Add(root))
                    {
                        warnings.Add($"Multiple storage locations share the same drive root '{root}'. Failover requires separate physical drives to be effective.");
                    }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not inspect drive root for path '{loc.Path}': {ex.Message}");
            }
        }

        // AutoPause validation
        if (settings.AutoPause.AwayMinutes is < 1 or > 240)
        {
            errors.Add("Away timeout must be between 1 and 240 minutes.");
        }

        if (settings.AutoPause.PausedReminderMinutes < 0 || settings.AutoPause.PausedReminderMinutes > 60)
        {
            errors.Add("Paused reminder interval must be between 0 (off) and 60 minutes.");
        }

        // Advanced watchdog
        if (settings.Advanced.FfmpegMemoryRestartMb <= settings.Advanced.FfmpegMemoryWarnMb)
        {
            errors.Add("FFmpeg memory restart threshold must be above the warning threshold.");
        }

        // Playback: custom player path must exist when Custom is chosen
        if (settings.Playback.Player == PlaybackPlayer.Custom &&
            (string.IsNullOrWhiteSpace(settings.Playback.CustomPlayerPath) ||
             !File.Exists(settings.Playback.CustomPlayerPath)))
        {
            errors.Add("Custom player path must point to an existing executable.");
        }

        // Work reminder time validation
        if (!TryParseTime(settings.Reminders.WorkStart, out _))
            errors.Add($"Work start time '{settings.Reminders.WorkStart}' is not a valid HH:mm value.");

        if (!TryParseTime(settings.Reminders.WorkEnd, out _))
            errors.Add($"Work end time '{settings.Reminders.WorkEnd}' is not a valid HH:mm value.");

        return new ValidationResult(errors.Count == 0, errors, warnings);
    }

    private static bool TryParseTime(string? value, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (TimeSpan.TryParseExact(value, "hh\\:mm", null, out result)) return true;
        if (TimeSpan.TryParseExact(value, "h\\:mm", null, out result)) return true;
        return false;
    }
}
