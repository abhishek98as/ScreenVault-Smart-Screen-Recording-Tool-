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

        if (settings.Storage.SplitMinutes is < 1 or > 1440)
        {
            errors.Add("File split duration must be between 1 and 1440 minutes.");
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

        return new ValidationResult(errors.Count == 0, errors, warnings);
    }
}
