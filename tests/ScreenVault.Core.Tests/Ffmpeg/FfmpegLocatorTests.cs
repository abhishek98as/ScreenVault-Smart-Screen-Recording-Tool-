using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Ffmpeg;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Ffmpeg;

public sealed class FfmpegLocatorTests
{
    [Fact]
    public void Locate_FindsBinaries_WhenPresentInCustomPath()
    {
        var customFfmpeg = @"C:\custom\ffmpeg.exe";
        var customFfprobe = @"C:\custom\ffprobe.exe";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [customFfmpeg] = new MockFileData("fake ffmpeg"),
            [customFfprobe] = new MockFileData("fake ffprobe")
        });

        var locator = new FfmpegLocator(fileSystem);
        var paths = locator.Locate(customFfmpeg);

        paths.FfmpegPath.ShouldBe(customFfmpeg);
        paths.FfprobePath.ShouldBe(customFfprobe);
    }

    [Fact]
    public void Locate_ThrowsFileNotFoundException_WhenBinariesNotFound()
    {
        var fileSystem = new MockFileSystem();
        var locator = new FfmpegLocator(fileSystem);

        Should.Throw<FileNotFoundException>(() => locator.Locate(@"C:\nonexistent\ffmpeg.exe"));
    }
}
