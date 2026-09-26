using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Storage;

public sealed class RetentionServiceTests
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly SessionStore _sessionStore;
    private readonly SettingsService _settingsService;

    public RetentionServiceTests()
    {
        _sessionStore = new SessionStore(_fileSystem, @"C:\ScreenVault\sessions");
        _settingsService = new SettingsService(@"C:\ScreenVault\settings.json", isPortable: false, _fileSystem);
    }

    [Fact]
    public async Task RunCleanupAsync_RetentionDisabled_DoesNothing()
    {
        var settings = _settingsService.Current;
        settings.Storage.Retention.Enabled = false;
        _settingsService.Save(settings);

        var manifest = new SessionManifest
        {
            SessionId = "old-session",
            Status = "Completed",
            StartedAtUtc = DateTime.UtcNow.AddDays(-60)
        };
        _sessionStore.Save(manifest);

        using var service = new RetentionService(_sessionStore, _settingsService, _fileSystem);
        var deleted = await service.RunCleanupAsync();

        deleted.ShouldBe(0);
        _sessionStore.Load("old-session").ShouldNotBeNull();
    }

    [Fact]
    public async Task RunCleanupAsync_ExpiredSession_IsDeleted()
    {
        var settings = _settingsService.Current;
        settings.Storage.Retention.Enabled = true;
        settings.Storage.Retention.KeepDays = 30;
        _settingsService.Save(settings);

        var segPath = @"C:\Recordings\2026-08-01\SV_2026-08-01_10-00-00_p001.mkv";
        _fileSystem.AddFile(segPath, new MockFileData("video data"));

        var manifest = new SessionManifest
        {
            SessionId = "expired-session",
            Status = "Completed",
            StartedAtUtc = DateTime.UtcNow.AddDays(-45)
        };
        manifest.Segments.Add(new SegmentManifestEntry
        {
            Index = 1,
            Location = @"C:\Recordings",
            FinalPath = segPath
        });
        _sessionStore.Save(manifest);

        using var service = new RetentionService(_sessionStore, _settingsService, _fileSystem);
        var deleted = await service.RunCleanupAsync();

        deleted.ShouldBe(1);
        _fileSystem.File.Exists(segPath).ShouldBeFalse();
        _sessionStore.Load("expired-session").ShouldBeNull();
    }

    [Fact]
    public async Task RunCleanupAsync_ProtectedSession_IsNotDeleted()
    {
        var settings = _settingsService.Current;
        settings.Storage.Retention.Enabled = true;
        settings.Storage.Retention.KeepDays = 30;
        _settingsService.Save(settings);

        var manifest = new SessionManifest
        {
            SessionId = "protected-session",
            Status = "Completed",
            StartedAtUtc = DateTime.UtcNow.AddDays(-45),
            Protected = true
        };
        _sessionStore.Save(manifest);

        using var service = new RetentionService(_sessionStore, _settingsService, _fileSystem);
        var deleted = await service.RunCleanupAsync();

        deleted.ShouldBe(0);
        _sessionStore.Load("protected-session").ShouldNotBeNull();
    }

    [Fact]
    public async Task RunCleanupAsync_SessionWithMarkers_IsNotDeletedWhenConfigured()
    {
        var settings = _settingsService.Current;
        settings.Storage.Retention.Enabled = true;
        settings.Storage.Retention.KeepDays = 30;
        settings.Storage.Retention.ProtectSessionsWithMarkers = true;
        _settingsService.Save(settings);

        var manifest = new SessionManifest
        {
            SessionId = "marked-session",
            Status = "Completed",
            StartedAtUtc = DateTime.UtcNow.AddDays(-45)
        };
        manifest.Markers.Add(new MarkerEntry
        {
            AtUtc = DateTime.UtcNow.AddDays(-45).AddMinutes(5),
            Note = "Important project discussion",
            Kind = "User"
        });
        _sessionStore.Save(manifest);

        using var service = new RetentionService(_sessionStore, _settingsService, _fileSystem);
        var deleted = await service.RunCleanupAsync();

        deleted.ShouldBe(0);
        _sessionStore.Load("marked-session").ShouldNotBeNull();
    }
}
