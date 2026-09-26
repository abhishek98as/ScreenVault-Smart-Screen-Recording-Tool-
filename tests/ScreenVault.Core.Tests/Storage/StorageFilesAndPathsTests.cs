using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.Output;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Storage;

public sealed class StorageFilesAndPathsTests
{
    private sealed class FakeFfprobeClient : IFfprobeClient
    {
        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
        {
            var result = new MediaProbeResult
            {
                DurationSeconds = 60.0,
                Streams = [new ProbeStreamInfo { CodecType = "video", Width = 1920, Height = 1080 }]
            };
            return Task.FromResult<MediaProbeResult?>(result);
        }

        public Task<IReadOnlyList<double>> GetKeyframePtsAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<double>>([0.0, 5.0, 10.0]);

        public bool ValidateRemux(MediaProbeResult tsProbe, MediaProbeResult remuxProbe, out string? failureReason)
        {
            failureReason = null;
            return true;
        }
    }

    [Fact]
    public void StorageDirectoryHelper_MetadataAndTempDirectories_AreUnderScreenVaultFolder()
    {
        var mockFs = new MockFileSystem();
        var storageRoot = @"C:\Recordings";
        mockFs.AddDirectory(storageRoot);

        var metaDir = StorageDirectoryHelper.GetMetadataDirectory(mockFs, storageRoot);
        var tempDir = StorageDirectoryHelper.GetTempDirectory(mockFs, storageRoot);

        metaDir.ShouldBe(@"C:\Recordings\.screenvault\sessions");
        tempDir.ShouldBe(@"C:\Recordings\.screenvault\tmp");

        mockFs.Directory.Exists(metaDir).ShouldBeTrue();
        mockFs.Directory.Exists(tempDir).ShouldBeTrue();
    }

    [Fact]
    public void StorageDirectoryHelper_GetLocationRoot_ResolvesStorageRootCorrectly()
    {
        var mockFs = new MockFileSystem();
        var storageRoot = @"C:\Recordings";
        var dayFolder = @"C:\Recordings\2026-09-25";
        var videoFile = @"C:\Recordings\2026-09-25\SV_2026-09-25_10-00-00_p001.mkv";
        mockFs.AddDirectory(dayFolder);
        mockFs.AddFile(videoFile, new MockFileData("data"));

        var resolvedRoot = StorageDirectoryHelper.GetLocationRoot(mockFs, videoFile, [storageRoot]);
        resolvedRoot.ShouldBe(@"C:\Recordings");
    }

    [Fact]
    public void StorageDirectoryHelper_CleanupTempDirectory_RemovesOldFiles()
    {
        var mockFs = new MockFileSystem();
        var storageRoot = @"C:\Recordings";
        var tempDir = StorageDirectoryHelper.GetTempDirectory(mockFs, storageRoot);

        var oldFile = mockFs.Path.Combine(tempDir, "old.partial");
        var newFile = mockFs.Path.Combine(tempDir, "new.partial");

        mockFs.AddFile(oldFile, new MockFileData("old data") { LastWriteTime = DateTime.UtcNow.AddHours(-30) });
        mockFs.AddFile(newFile, new MockFileData("new data") { LastWriteTime = DateTime.UtcNow.AddMinutes(-10) });

        StorageDirectoryHelper.CleanupTempDirectory(mockFs, storageRoot, TimeSpan.FromHours(24));

        mockFs.File.Exists(oldFile).ShouldBeFalse();
        mockFs.File.Exists(newFile).ShouldBeTrue();
    }

    [Fact]
    public void StorageDirectoryHelper_MigrateLegacyFolder_MovesManifestAndMergesMarkers()
    {
        var mockFs = new MockFileSystem();
        var storageRoot = @"C:\Recordings";
        var dayFolder = @"C:\Recordings\2026-09-25";
        mockFs.AddDirectory(dayFolder);

        var legacySessionPath = mockFs.Path.Combine(dayFolder, "SV_2026-09-25_10-00-00.session.json");
        var manifest = new SessionManifest
        {
            SessionId = "2026-09-25_10-00-00",
            Status = "Completed",
            StartedAtUtc = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc)
        };
        var store = new SessionStore(mockFs, @"C:\AppData\ScreenVault\sessions");
        store.Save(manifest, [dayFolder]);

        // Legacy markers.txt in day folder
        var markersTxtPath = mockFs.Path.Combine(dayFolder, "markers.txt");
        mockFs.AddFile(markersTxtPath, new MockFileData("10:05:00  [marker]  Legacy note\n"));

        StorageDirectoryHelper.MigrateLegacyFolder(mockFs, storageRoot, store);

        // Day folder should no longer contain .session.json or markers.txt
        mockFs.File.Exists(legacySessionPath).ShouldBeFalse();
        mockFs.File.Exists(markersTxtPath).ShouldBeFalse();

        // Manifest should be migrated to .screenvault\sessions
        var migratedManifestPath = @"C:\Recordings\.screenvault\sessions\SV_2026-09-25_10-00-00.session.json";
        mockFs.File.Exists(migratedManifestPath).ShouldBeTrue();

        // Loaded manifest must contain the migrated marker
        var loaded = store.Load("2026-09-25_10-00-00");
        loaded.ShouldNotBeNull();
        loaded.Markers.ShouldContain(m => m.Note == "Legacy note");
    }

    [Fact]
    public async Task RecoveryService_RebuildsMissingManifest_FromOrphanVideoFiles()
    {
        var mockFs = new MockFileSystem();
        var storageRoot = @"C:\Recordings";
        var dayFolder = @"C:\Recordings\2026-09-25";
        mockFs.AddDirectory(dayFolder);

        var video1 = mockFs.Path.Combine(dayFolder, "SV_2026-09-25_14-30-00_p001.mkv");
        var video2 = mockFs.Path.Combine(dayFolder, "SV_2026-09-25_14-30-00_p002.mkv");
        mockFs.AddFile(video1, new MockFileData("part 1 data"));
        mockFs.AddFile(video2, new MockFileData("part 2 data"));

        var store = new SessionStore(mockFs, @"C:\AppData\ScreenVault\sessions");
        var storageSettings = new StorageSettings
        {
            Locations = [new StorageLocationConfig { Path = storageRoot, Enabled = true }]
        };
        var fakeFfprobe = new FakeFfprobeClient();
        var postProcessor = new PostProcessor(fakeFfprobe, new ChapterWriter(mockFs), store, mockFs, @"C:\ffmpeg.exe");
        var recoveryService = new RecoveryService(store, storageSettings, postProcessor, mockFs, ffmpegPath: @"C:\ffmpeg.exe", ffprobeClient: fakeFfprobe);

        // Initially no manifest exists
        store.Load("2026-09-25_14-30-00").ShouldBeNull();

        await recoveryService.RunRecoveryAsync();

        // Manifest must now be rebuilt and available
        var rebuilt = store.Load("2026-09-25_14-30-00");
        rebuilt.ShouldNotBeNull();
        rebuilt.SessionId.ShouldBe("2026-09-25_14-30-00");
        rebuilt.Segments.Count.ShouldBe(2);
        rebuilt.Segments[0].Index.ShouldBe(1);
        rebuilt.Segments[1].Index.ShouldBe(2);
        rebuilt.Segments[0].DurationSec.ShouldBe(60.0);
    }

    [Fact]
    public void PathHandling_NoDotDotInRelativeOrFinalPaths()
    {
        var locationRoot = @"C:\Users\tester\Videos\Screen Recordings";
        var expLocation = Path.GetFullPath(Environment.ExpandEnvironmentVariables(locationRoot));
        var fullTs = Path.GetFullPath(Path.Combine(expLocation, @"2026-09-25\SV_2026-09-25_10-00-00_p001.ts"));
        var fullFinal = Path.GetFullPath(Path.Combine(expLocation, @"2026-09-25\SV_2026-09-25_10-00-00_p001.mkv"));

        var relTs = Path.GetRelativePath(expLocation, fullTs);
        var relFinal = Path.GetRelativePath(expLocation, fullFinal);

        relTs.ShouldNotContain("..");
        relFinal.ShouldNotContain("..");

        var resolvedTs = Path.GetFullPath(Path.Combine(expLocation, relTs));
        var resolvedFinal = Path.GetFullPath(Path.Combine(expLocation, relFinal));

        resolvedTs.ShouldBe(fullTs);
        resolvedFinal.ShouldBe(fullFinal);
    }

    [Fact]
    public void UserProfilePath_ExpandsAndResolvesWithoutDotDot()
    {
        var rawPath = @"%USERPROFILE%\Videos\Screen Recordings";
        var expanded = Environment.ExpandEnvironmentVariables(rawPath);

        Path.IsPathRooted(expanded).ShouldBeTrue();
        expanded.ShouldNotContain("%USERPROFILE%");
        expanded.ShouldNotContain("..");

        var full = Path.GetFullPath(expanded);
        full.ShouldNotContain("..");
    }

    [Fact]
    public void StorageDirectoryHelper_GetTempDirectory_NeverTargetsDayFolder()
    {
        var mockFs = new MockFileSystem();
        var storageRoot = @"C:\Recordings";
        var dayFolder = @"C:\Recordings\2026-09-25";
        var videoPath = @"C:\Recordings\2026-09-25\SV_2026-09-25_10-00-00_p001.mkv";
        mockFs.AddDirectory(dayFolder);

        var locationRoot = StorageDirectoryHelper.GetLocationRoot(mockFs, videoPath, [storageRoot]);
        var tempDir = StorageDirectoryHelper.GetTempDirectory(mockFs, locationRoot);

        tempDir.ShouldBe(@"C:\Recordings\.screenvault\tmp");
        tempDir.ShouldNotContain("2026-09-25");
        tempDir.ShouldNotContain("..");
    }
}
