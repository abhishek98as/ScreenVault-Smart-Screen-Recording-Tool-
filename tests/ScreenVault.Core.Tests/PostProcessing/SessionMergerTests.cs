using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Sessions;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.PostProcessing;

public sealed class SessionMergerTests
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly SessionStore _sessionStore;
    private readonly ChapterWriter _chapterWriter;
    private readonly FfprobeClient _ffprobeClient;

    public SessionMergerTests()
    {
        _sessionStore = new SessionStore(_fileSystem, @"C:\ScreenVault\sessions");
        _chapterWriter = new ChapterWriter(_fileSystem);
        _ffprobeClient = new FfprobeClient(@"C:\tools\ffprobe.exe");
    }

    [Fact]
    public async Task MergeSessionAsync_SessionNotFound_ReturnsError()
    {
        var merger = new SessionMerger(_sessionStore, _ffprobeClient, _chapterWriter, _fileSystem, @"C:\tools\ffmpeg.exe");

        var result = await merger.MergeSessionAsync("nonexistent-session");

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Fact]
    public async Task MergeSessionAsync_NoSegments_ReturnsError()
    {
        var manifest = new SessionManifest
        {
            SessionId = "empty-session",
            StartedAtUtc = DateTime.UtcNow
        };
        _sessionStore.Save(manifest);

        var merger = new SessionMerger(_sessionStore, _ffprobeClient, _chapterWriter, _fileSystem, @"C:\tools\ffmpeg.exe");

        var result = await merger.MergeSessionAsync("empty-session");

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("no recorded segments");
    }

    [Fact]
    public async Task MergeSessionAsync_MissingSegmentFile_ReturnsError()
    {
        var manifest = new SessionManifest
        {
            SessionId = "partial-session",
            StartedAtUtc = DateTime.UtcNow
        };
        manifest.Segments.Add(new SegmentManifestEntry
        {
            Index = 1,
            Location = @"C:\Recordings",
            FinalPath = @"C:\Recordings\segment1.mkv",
            StartedAtUtc = DateTime.UtcNow,
            EndedAtUtc = DateTime.UtcNow.AddMinutes(10)
        });
        _sessionStore.Save(manifest);

        var merger = new SessionMerger(_sessionStore, _ffprobeClient, _chapterWriter, _fileSystem, @"C:\tools\ffmpeg.exe");

        var result = await merger.MergeSessionAsync("partial-session");

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Missing segment file");
    }
}
