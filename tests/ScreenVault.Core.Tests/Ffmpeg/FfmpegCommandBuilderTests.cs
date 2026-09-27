using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Settings;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Ffmpeg;

public sealed class FfmpegCommandBuilderTests
{
    [Fact]
    public void Build_ConstructsValidArguments_ForDefaultSettings()
    {
        var settings = AppSettings.CreateDefault();
        var profile = EncoderProfile.Create("x264", VideoQuality.Balanced, 15);

        var spec = FfmpegCommandBuilder.Build(@"C:\tools\ffmpeg\ffmpeg.exe", settings, profile, includeAudio: true);

        spec.ExecutablePath.ShouldBe(@"C:\tools\ffmpeg\ffmpeg.exe");
        spec.Arguments.ShouldContain("-progress");
        spec.Arguments.ShouldContain("pipe:2");
        spec.Arguments.ShouldContain("ddagrab=output_idx=0:framerate=15:draw_mouse=1,hwdownload,format=bgra");
        spec.Arguments.ShouldContain("pipe:0");
        spec.Arguments.ShouldContain("pipe:1");
        spec.Arguments.ShouldContain("mpegts");
        spec.Arguments.ShouldContain("libx264");
        spec.Arguments.ShouldContain("ultrafast");
        spec.Arguments.ShouldContain("zerolatency");
        spec.Arguments.ShouldContain("-pix_fmt");
        spec.Arguments.ShouldContain("yuv420p");

        // Verify thread_queue_size is placed only immediately before audio pipe:0
        var argList = spec.Arguments.ToList();
        var tqIndex = argList.IndexOf("-thread_queue_size");
        tqIndex.ShouldBeGreaterThan(-1);
        spec.Arguments[tqIndex + 1].ShouldBe("1024");
        spec.Arguments[tqIndex + 2].ShouldBe("-i");
        spec.Arguments[tqIndex + 3].ShouldBe("pipe:0");

        // Ensure video input (lavfi) does not have thread_queue_size before it
        var lavfiIndex = argList.IndexOf("lavfi");
        spec.Arguments.Take(lavfiIndex).ShouldNotContain("-thread_queue_size");
    }

    [Fact]
    public void Build_WithoutAudio_DoesNotIncludeAudioInputOrShortest()
    {
        var settings = AppSettings.CreateDefault();
        var profile = EncoderProfile.Create("nvenc-d3d11", VideoQuality.Balanced, 15);

        var spec = FfmpegCommandBuilder.Build(@"C:\tools\ffmpeg\ffmpeg.exe", settings, profile, includeAudio: false);

        spec.Arguments.ShouldNotContain("pipe:0");
        spec.Arguments.ShouldNotContain("-shortest");
        spec.Arguments.ShouldContain("h264_nvenc");
    }

    [Fact]
    public void Build_WithDownscaling_IncludesScaleFilter()
    {
        var settings = AppSettings.CreateDefault();
        settings.Video.DownscaleTo1080p = true;
        var profile = EncoderProfile.Create("nvenc-d3d11", VideoQuality.Balanced, 15);

        var spec = FfmpegCommandBuilder.Build(@"C:\tools\ffmpeg\ffmpeg.exe", settings, profile, includeAudio: false);

        // Caps the width at 1920 px (never upscales smaller screens) and keeps the aspect ratio.
        spec.Arguments.Any(a => a.Contains("scale=w='trunc(min(1920,iw)/2)*2':h=-2", StringComparison.Ordinal)).ShouldBeTrue();
    }
}
