using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Settings;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Settings;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Constructor_GeneratesDefaultSettings_WhenFileDoesNotExist()
    {
        var fileSystem = new MockFileSystem();
        var service = new SettingsService(customSettingsPath: @"C:\ScreenVault\settings.json", fileSystem: fileSystem);

        service.Current.ShouldNotBeNull();
        service.Current.Video.FrameRate.ShouldBe(15);
        service.Current.Storage.SplitMinutes.ShouldBe(10);
        fileSystem.File.Exists(@"C:\ScreenVault\settings.json").ShouldBeTrue();
    }

    [Fact]
    public void Save_PersistsSettings_AndUpdatesCurrent()
    {
        var fileSystem = new MockFileSystem();
        var service = new SettingsService(customSettingsPath: @"C:\ScreenVault\settings.json", fileSystem: fileSystem);

        var newSettings = service.Current.Clone();
        newSettings.Video.FrameRate = 30;
        service.Save(newSettings);

        service.Current.Video.FrameRate.ShouldBe(30);

        var reloadedService = new SettingsService(customSettingsPath: @"C:\ScreenVault\settings.json", fileSystem: fileSystem);
        reloadedService.Current.Video.FrameRate.ShouldBe(30);
    }

    [Fact]
    public void Load_FallsBackToBackup_WhenPrimaryIsCorrupt()
    {
        var primaryPath = @"C:\ScreenVault\settings.json";
        var backupPath = @"C:\ScreenVault\settings.json.bak";

        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [primaryPath] = new MockFileData("CORRUPT JSON {{{"),
            [backupPath] = new MockFileData("{\"schemaVersion\":1,\"video\":{\"frameRate\":24}}")
        });

        var service = new SettingsService(customSettingsPath: primaryPath, fileSystem: fileSystem);

        service.Current.Video.FrameRate.ShouldBe(24);
    }

    [Fact]
    public void SettingsValidator_RejectsInvalidFrameRate()
    {
        var settings = AppSettings.CreateDefault();
        settings.Video.FrameRate = 120; // max 60

        var result = SettingsValidator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("frame rate"));
    }

    [Fact]
    public void Load_V1SettingsJson_MigratesAndYieldsDefaultAudioModes()
    {
        var primaryPath = @"C:\ScreenVault\settings.json";
        // Real v1 JSON (schemaVersion 1, no audio section)
        var v1Json = """
        {
          "schemaVersion": 1,
          "general": { "startWithWindows": true, "startRecordingOnLaunch": true },
          "video": { "frameRate": 30, "quality": "Balanced", "encoder": "Auto" },
          "storage": { "splitMinutes": 10 }
        }
        """;

        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [primaryPath] = new MockFileData(v1Json)
        });

        var service = new SettingsService(customSettingsPath: primaryPath, fileSystem: fileSystem);

        service.Current.SchemaVersion.ShouldBe(2);
        service.Current.Audio.ShouldNotBeNull();
        service.Current.Audio.MicMode.ShouldBe(MicMode.DefaultCommunications);
        service.Current.Audio.OutputMode.ShouldBe(OutputMode.DefaultPlusCommunications);
    }

    [Fact]
    public void Load_V2SettingsJson_DeserializesStringEnumsCorrectly()
    {
        var primaryPath = @"C:\ScreenVault\settings.json";
        var v2Json = """
        {
          "schemaVersion": 2,
          "general": { "startWithWindows": true, "startRecordingOnLaunch": false },
          "video": { "frameRate": 15, "quality": "Balanced", "encoder": "Auto" },
          "audio": {
            "micMode": "DefaultCommunications",
            "outputMode": "DefaultPlusCommunications",
            "micGainDb": 0.0,
            "systemGainDb": 0.0,
            "jitterTargetMs": 100
          },
          "storage": { "splitMinutes": 10 }
        }
        """;

        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [primaryPath] = new MockFileData(v2Json)
        });

        var service = new SettingsService(customSettingsPath: primaryPath, fileSystem: fileSystem);

        service.Current.SchemaVersion.ShouldBe(2);
        service.Current.Audio.MicMode.ShouldBe(MicMode.DefaultCommunications);
        service.Current.Audio.OutputMode.ShouldBe(OutputMode.DefaultPlusCommunications);
    }
}
