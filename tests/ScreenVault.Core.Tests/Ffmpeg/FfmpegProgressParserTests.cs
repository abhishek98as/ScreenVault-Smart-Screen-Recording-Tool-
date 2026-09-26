using ScreenVault.Core.Ffmpeg;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Ffmpeg;

public sealed class FfmpegProgressParserTests
{
    [Fact]
    public void ParseLine_ParsesFrameFpsSpeedAndOutTime()
    {
        var parser = new FfmpegProgressParser();

        parser.ParseLine("frame=300", out var isDone1);
        isDone1.ShouldBeFalse();

        parser.ParseLine("fps=15.00", out _);
        parser.ParseLine("speed=1.02x", out _);
        parser.ParseLine("out_time_us=20000000", out _);
        parser.ParseLine("total_size=1048576", out _);
        var progress = parser.ParseLine("progress=continue", out var isDone2);

        isDone2.ShouldBeTrue();
        progress.Frame.ShouldBe(300);
        progress.Fps.ShouldBe(15.0);
        progress.Speed.ShouldBe(1.02);
        progress.OutTime.ShouldBe(TimeSpan.FromSeconds(20));
        progress.TotalBytes.ShouldBe(1048576);
    }
}
