using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Settings;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Settings;

public sealed class SettingsMigratorTests
{
    [Fact]
    public void Migrate_FromV1_SetsV2DefaultsAndMigratesFps()
    {
        var mockFs = new MockFileSystem();
        var settingsPath = @"C:\AppData\ScreenVault\settings.json";
        mockFs.AddFile(settingsPath, new MockFileData("{\"SchemaVersion\":1}"));

        var v1Settings = new AppSettings
        {
            SchemaVersion = 1,
            General = new GeneralSettings { StartRecordingOnLaunch = true },
            Video = new VideoSettings { FrameRate = 30 }
        };

        var migrated = SettingsMigrator.Migrate(v1Settings, settingsPath, mockFs);

        migrated.SchemaVersion.ShouldBe(2);
        migrated.General.StartRecordingOnLaunch.ShouldBeFalse();
        migrated.Video.FrameRate.ShouldBe(15);
        migrated.Saving.ShouldNotBeNull();
        migrated.Saving.MergeOnSave.ShouldBeTrue();
        migrated.Playback.ShouldNotBeNull();
        migrated.Playback.Player.ShouldBe(PlaybackPlayer.Auto);
        migrated.MeetingDetection.ShouldNotBeNull();
        migrated.Reminders.ShouldNotBeNull();
        migrated.Transcription.ShouldNotBeNull();

        // Verify backup was created
        mockFs.File.Exists(@"C:\AppData\ScreenVault\settings.v1.bak").ShouldBeTrue();
    }

    [Fact]
    public void Migrate_AlreadyV2_DoesNotModify()
    {
        var v2Settings = new AppSettings
        {
            SchemaVersion = 2,
            General = new GeneralSettings { StartRecordingOnLaunch = true },
            Video = new VideoSettings { FrameRate = 24 }
        };

        var migrated = SettingsMigrator.Migrate(v2Settings);

        migrated.SchemaVersion.ShouldBe(2);
        migrated.General.StartRecordingOnLaunch.ShouldBeTrue();
        migrated.Video.FrameRate.ShouldBe(24);
    }
}
