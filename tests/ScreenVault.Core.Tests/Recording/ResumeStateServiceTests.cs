using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Recording;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Recording;

public sealed class ResumeStateServiceTests
{
    [Fact]
    public void SaveAndLoad_ValidResumeStateWithinAge_ReturnsState()
    {
        var fs = new MockFileSystem();
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        ResumeStateService.SaveResumeState(wasRecording: true, fileSystem: fs, utcNow: now);

        var loaded = ResumeStateService.TryGetValidResumeState(
            maxAge: TimeSpan.FromMinutes(15),
            fileSystem: fs,
            utcNow: now.AddMinutes(5));

        loaded.ShouldNotBeNull();
        loaded.WasRecording.ShouldBeTrue();
        loaded.AtUtc.ShouldBe(now);
    }

    [Fact]
    public void TryGetValidResumeState_WhenExpired_ReturnsNull()
    {
        var fs = new MockFileSystem();
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        ResumeStateService.SaveResumeState(wasRecording: true, fileSystem: fs, utcNow: now);

        // 16 minutes later (> 15 minutes)
        var loaded = ResumeStateService.TryGetValidResumeState(
            maxAge: TimeSpan.FromMinutes(15),
            fileSystem: fs,
            utcNow: now.AddMinutes(16));

        loaded.ShouldBeNull();
    }

    [Fact]
    public void TryGetValidResumeState_WhenWasNotRecording_ReturnsNull()
    {
        var fs = new MockFileSystem();
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        ResumeStateService.SaveResumeState(wasRecording: false, fileSystem: fs, utcNow: now);

        var loaded = ResumeStateService.TryGetValidResumeState(
            maxAge: TimeSpan.FromMinutes(15),
            fileSystem: fs,
            utcNow: now.AddMinutes(1));

        loaded.ShouldBeNull();
    }

    [Fact]
    public void ClearResumeState_DeletesFile()
    {
        var fs = new MockFileSystem();
        ResumeStateService.SaveResumeState(wasRecording: true, fileSystem: fs);

        var path = ResumeStateService.GetResumeFilePath(fs);
        fs.File.Exists(path).ShouldBeTrue();

        ResumeStateService.ClearResumeState(fs);
        fs.File.Exists(path).ShouldBeFalse();
    }
}
