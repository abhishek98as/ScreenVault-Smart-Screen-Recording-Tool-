using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Sessions;
using Xunit;

namespace ScreenVault.Core.Tests.Sessions;

public class SessionStoreAndMarkerTests
{
    [Fact]
    public void SessionStore_SaveAndLoad_RoundtripsSuccessfully()
    {
        var mockFs = new MockFileSystem();
        var store = new SessionStore(mockFs, @"C:\AppData\ScreenVault\sessions");

        var manifest = new SessionManifest
        {
            SessionId = "2026-09-23_10-00-00",
            Status = "Recording",
            StartedAtUtc = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc),
            Machine = "TEST-PC",
            AppVersion = "1.0.0"
        };
        manifest.Segments.Add(new SegmentManifestEntry
        {
            Index = 1,
            Location = @"C:\Recordings",
            TsPath = @"2026-09-23\SV_p001.ts",
            FinalPath = @"2026-09-23\SV_p001.mkv",
            StartedAtUtc = manifest.StartedAtUtc,
            Bytes = 1048576,
            OpenReason = "SessionStart"
        });

        store.Save(manifest, [@"C:\Recordings\2026-09-23"]);

        // Check canonical file exists
        var canonicalPath = store.GetCanonicalPath("2026-09-23_10-00-00");
        Assert.True(mockFs.FileExists(canonicalPath));

        // Check mirror file exists
        var mirrorPath = @"C:\Recordings\2026-09-23\SV_2026-09-23_10-00-00.session.json";
        Assert.True(mockFs.FileExists(mirrorPath));

        // Load back
        var loaded = store.Load("2026-09-23_10-00-00");
        Assert.NotNull(loaded);
        Assert.Equal("2026-09-23_10-00-00", loaded.SessionId);
        Assert.Single(loaded.Segments);
        Assert.Equal(1048576, loaded.Segments[0].Bytes);
    }

    [Fact]
    public void MarkerService_AddsMarker_UpdatesManifestAndDoesNotWriteMarkersTxt()
    {
        var mockFs = new MockFileSystem();
        var store = new SessionStore(mockFs, @"C:\AppData\ScreenVault\sessions");
        var markerService = new MarkerService(store, fileSystem: mockFs);

        var manifest = new SessionManifest
        {
            SessionId = "2026-09-23_10-00-00",
            Status = "Recording",
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        };

        var backupDir = @"C:\Recordings\.screenvault\sessions";
        markerService.SetActiveSession(manifest, backupDir);

        var marker = markerService.AddMarker("Discuss roadmap", "User");

        Assert.Equal("Discuss roadmap", marker.Note);
        Assert.True(marker.OffsetSec >= 590); // ~600 seconds
        Assert.Single(manifest.Markers);

        // Verify markers.txt is NOT written in any day folder
        var dayFolder = @"C:\Recordings\2026-09-23";
        var markersTxtPath = mockFs.Path.Combine(dayFolder, "markers.txt");
        Assert.False(mockFs.FileExists(markersTxtPath));

        // Verify manifest was persisted to canonical and backup
        var canonicalPath = store.GetCanonicalPath("2026-09-23_10-00-00");
        Assert.True(mockFs.FileExists(canonicalPath));
        var backupPath = mockFs.Path.Combine(backupDir, "SV_2026-09-23_10-00-00.session.json");
        Assert.True(mockFs.FileExists(backupPath));
    }

    [Fact]
    public void SessionStore_Delete_RemovesCanonicalAndMirrorFiles()
    {
        var mockFs = new MockFileSystem();
        var store = new SessionStore(mockFs, @"C:\AppData\ScreenVault\sessions");

        var manifest = new SessionManifest
        {
            SessionId = "2026-09-23_12-00-00",
            Status = "Completed",
            StartedAtUtc = DateTime.UtcNow
        };
        var backupDir = @"C:\Recordings\.screenvault\sessions";
        store.Save(manifest, [backupDir]);

        var canonicalPath = store.GetCanonicalPath(manifest.SessionId);
        var backupPath = mockFs.Path.Combine(backupDir, $"SV_{manifest.SessionId}.session.json");

        Assert.True(mockFs.FileExists(canonicalPath));
        Assert.True(mockFs.FileExists(backupPath));

        store.Delete(manifest.SessionId, [backupDir]);

        Assert.False(mockFs.FileExists(canonicalPath));
        Assert.False(mockFs.FileExists(backupPath));
    }

    [Fact]
    public void ChapterWriter_GeneratesFfmetadataFormat_WithEscapedValues()
    {
        var mockFs = new MockFileSystem();
        var writer = new ChapterWriter(mockFs);

        var startUtc = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
        var endUtc = new DateTime(2026, 9, 23, 10, 10, 0, DateTimeKind.Utc);

        var markers = new List<MarkerEntry>
        {
            new()
            {
                AtUtc = startUtc.AddSeconds(120),
                OffsetSec = 120,
                Note = "Intro = section #1; test\\note"
            },
            new()
            {
                AtUtc = startUtc.AddSeconds(300),
                OffsetSec = 300,
                Note = "Feature demo"
            }
        };

        var chapterPath = @"C:\Recordings\chapters.txt";
        var result = writer.WriteSegmentChapters("ScreenVault 2026-09-23", startUtc, endUtc, markers, chapterPath);

        Assert.NotNull(result);
        Assert.True(mockFs.FileExists(chapterPath));

        var text = mockFs.File.ReadAllText(chapterPath);
        Assert.Contains(";FFMETADATA1", text, StringComparison.Ordinal);
        Assert.Contains("[CHAPTER]", text, StringComparison.Ordinal);
        Assert.Contains("START=120000", text, StringComparison.Ordinal);
        Assert.Contains("END=300000", text, StringComparison.Ordinal);
        // Verify escaping of =, #, ;, \
        Assert.Contains(@"title=Intro \= section \#1\; test\\note", text, StringComparison.Ordinal);
        Assert.Contains("START=300000", text, StringComparison.Ordinal);
    }
}
