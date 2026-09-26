using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Output;
using Shouldly;
using Xunit;

namespace ScreenVault.IntegrationTests;

public sealed class FfmpegIntegrationTests
{
    private sealed class SimpleTestSink : ISegmentSink
    {
        public MemoryStream Stream { get; } = new();
        public SegmentInfo? Current => null;
        public long BytesWritten => Stream.Length;

        public void BeginStream(DateTime? sessionStartLocal = null, int initialPartIndex = 1) { }

        public void Write(ReadOnlySpan<byte> tsBytes)
        {
            Stream.Write(tsBytes);
        }

        public void RequestRotation(RotationReason reason) { }

        public void EndStream() { }

#pragma warning disable CS0067
        public event EventHandler<SegmentInfo>? SegmentClosed;
#pragma warning restore CS0067
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EncoderProbe_ReturnsValidProfile_OnRealMachine()
    {
        var locator = new FfmpegLocator();
        var paths = locator.Locate();

        var result = await EncoderProbe.ProbeAsync(paths.FfmpegPath);

        result.ShouldNotBeNull();
        result.ProfileName.ShouldNotBeNullOrEmpty();
        result.Fingerprint.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FfmpegHost_CanStreamMpegTsFromProcess()
    {
        var locator = new FfmpegLocator();
        var paths = locator.Locate();

        var args = new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-f", "lavfi",
            "-i", "testsrc=size=640x360:rate=15",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-pix_fmt", "yuv420p",
            "-flush_packets", "1",
            "-f", "mpegts",
            "pipe:1"
        };
        var spec = new FfmpegLaunchSpec(paths.FfmpegPath, args);

        var sink = new SimpleTestSink();
        await using var host = new FfmpegHost();

        await host.StartAsync(spec, sink, CancellationToken.None);

        var start = DateTime.UtcNow;
        while (host.StdoutBytesTotal == 0 && (DateTime.UtcNow - start).TotalSeconds < 5)
        {
            await Task.Delay(200);
        }

        host.StdoutBytesTotal.ShouldBeGreaterThan(0);
        sink.Stream.Length.ShouldBeGreaterThan(0);

        await host.StopAsync(TimeSpan.FromSeconds(2));
        host.IsRunning.ShouldBeFalse();
    }
}
