using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Settings;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Settings;

public sealed class InstallDefaultsServiceTests
{
    [Fact]
    public void LoadDefaults_ParsesValidJsonCorrectly()
    {
        var fs = new MockFileSystem();
        var json = """
        {
          "installScope": "AllUsers",
          "installerVersion": "1.2.0",
          "defaultsRevision": "2026-09-25T14:05:11",
          "primaryLocation": "D:\\MyRecordings",
          "backupLocation": "E:\\ScreenVault Backup",
          "startWithWindows": true,
          "startRecordingOnLaunch": true
        }
        """;
        fs.AddFile("C:\\App\\install-defaults.json", new MockFileData(json));

        var defaults = InstallDefaultsService.LoadDefaults("C:\\App\\install-defaults.json", fs);

        defaults.ShouldNotBeNull();
        defaults.InstallScope.ShouldBe("AllUsers");
        defaults.InstallerVersion.ShouldBe("1.2.0");
        defaults.DefaultsRevision.ShouldBe("2026-09-25T14:05:11");
        defaults.PrimaryLocation.ShouldBe("D:\\MyRecordings");
        defaults.BackupLocation.ShouldBe("E:\\ScreenVault Backup");
        defaults.StartWithWindows.ShouldBeTrue();
        defaults.StartRecordingOnLaunch.ShouldBeTrue();
    }

    [Fact]
    public void LoadDefaults_MissingFileOrCorruptedJson_ReturnsNull()
    {
        var fs = new MockFileSystem();
        InstallDefaultsService.LoadDefaults("C:\\Missing\\defaults.json", fs).ShouldBeNull();

        fs.AddFile("C:\\Corrupted\\defaults.json", new MockFileData("{ invalid json"));
        InstallDefaultsService.LoadDefaults("C:\\Corrupted\\defaults.json", fs).ShouldBeNull();
    }

    [Fact]
    public void TryApplyDefaults_AppliesNewRevisionAndConfiguresStorageLocations()
    {
        var fs = new MockFileSystem();
        var settingsPath = "C:\\Data\\settings.json";
        var defaultsPath = "C:\\App\\install-defaults.json";

        var settingsService = new SettingsService(settingsPath, fileSystem: fs);

        var json = """
        {
          "installScope": "AllUsers",
          "installerVersion": "1.2.0",
          "defaultsRevision": "REV-2026-001",
          "primaryLocation": "C:\\CustomRecordings",
          "backupLocation": "D:\\CustomBackup",
          "startWithWindows": false,
          "startRecordingOnLaunch": true
        }
        """;
        fs.AddFile(defaultsPath, new MockFileData(json));

        var createdDirs = new List<string>();
        var applied = InstallDefaultsService.TryApplyDefaults(
            settingsService,
            defaultsFilePath: defaultsPath,
            fileSystem: fs,
            onDirectoriesCreated: dir => createdDirs.Add(dir));

        applied.ShouldBeTrue();
        settingsService.Current.AppliedDefaultsRevision.ShouldBe("REV-2026-001");
        settingsService.Current.General.StartWithWindows.ShouldBeFalse();
        settingsService.Current.General.StartRecordingOnLaunch.ShouldBeTrue();
        settingsService.Current.Storage.Locations.Count.ShouldBe(2);
        settingsService.Current.Storage.Locations[0].Path.ShouldBe("C:\\CustomRecordings");
        settingsService.Current.Storage.Locations[1].Path.ShouldBe("D:\\CustomBackup");

        // Subsequent call with the same revision returns false
        var reapplied = InstallDefaultsService.TryApplyDefaults(
            settingsService,
            defaultsFilePath: defaultsPath,
            fileSystem: fs);

        reapplied.ShouldBeFalse();
    }

    [Fact]
    public void TryApplyDefaults_WhenPrimaryLocationNull_UsesDefaultVideosLocation()
    {
        var fs = new MockFileSystem();
        var settingsPath = "C:\\Data\\settings.json";
        var defaultsPath = "C:\\App\\install-defaults.json";

        var settingsService = new SettingsService(settingsPath, fileSystem: fs);

        var json = """
        {
          "installScope": "PerUser",
          "installerVersion": "1.2.0",
          "defaultsRevision": "REV-2026-NULL-PRIMARY",
          "primaryLocation": null,
          "backupLocation": null,
          "startWithWindows": true,
          "startRecordingOnLaunch": false
        }
        """;
        fs.AddFile(defaultsPath, new MockFileData(json));

        var applied = InstallDefaultsService.TryApplyDefaults(
            settingsService,
            defaultsFilePath: defaultsPath,
            fileSystem: fs);

        applied.ShouldBeTrue();
        settingsService.Current.AppliedDefaultsRevision.ShouldBe("REV-2026-NULL-PRIMARY");
        settingsService.Current.Storage.Locations.Count.ShouldBe(1);
        settingsService.Current.Storage.Locations[0].Path.ShouldContain("Screen Recordings");
    }
}
