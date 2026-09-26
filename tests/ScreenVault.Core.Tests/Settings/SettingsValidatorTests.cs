using ScreenVault.Core.Settings;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Settings;

public sealed class SettingsValidatorTests
{
    [Fact]
    public void Validate_DefaultSettings_IsValid()
    {
        var settings = AppSettings.CreateDefault();
        var result = SettingsValidator.Validate(settings);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_InvalidFps_ReturnsError()
    {
        var settings = AppSettings.CreateDefault();
        settings.Video.FrameRate = 120; // Max is 60

        var result = SettingsValidator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("frame rate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_NoEnabledLocations_ReturnsError()
    {
        var settings = AppSettings.CreateDefault();
        foreach (var loc in settings.Storage.Locations)
        {
            loc.Enabled = false;
        }

        var result = SettingsValidator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("At least one storage location", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_InvalidSplitMinutes_ReturnsError()
    {
        var settings = AppSettings.CreateDefault();
        settings.Storage.SplitMinutes = 0;

        var result = SettingsValidator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("split duration", StringComparison.OrdinalIgnoreCase));
    }
}
