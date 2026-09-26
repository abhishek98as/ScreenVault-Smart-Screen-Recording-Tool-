using ScreenVault.Core.Audio;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Audio;

public sealed class JitterBufferTests
{
    [Fact]
    public void JitterBuffer_OutputsSilence_WhilePriming()
    {
        // 100 ms target = 4800 frames = 9600 floats
        var jitter = new JitterBuffer(targetMs: 100, sampleRate: 48000, channels: 2);

        // Feed only 100 frames (200 floats) -> below 4800 target
        var data = new float[200];
        Array.Fill(data, 0.5f);
        jitter.Write(data);

        var dest = new float[200];
        jitter.Read(dest, 100);

        // Must output silence because it is priming!
        dest.All(s => s == 0f).ShouldBeTrue();
    }

    [Fact]
    public void JitterBuffer_OutputsAudio_OncePrimed()
    {
        var jitter = new JitterBuffer(targetMs: 100, sampleRate: 48000, channels: 2);

        // Feed 5000 frames (10,000 floats) -> exceeds target 4800 frames
        var data = new float[10000];
        Array.Fill(data, 0.75f);
        jitter.Write(data);

        var dest = new float[200];
        jitter.Read(dest, 100);

        // Should output audio
        dest.All(s => s == 0.75f).ShouldBeTrue();
        jitter.Underflows.ShouldBe(0);
    }

    [Fact]
    public void JitterBuffer_UnderflowPadsSilence_AndRePrimes()
    {
        var jitter = new JitterBuffer(targetMs: 100, sampleRate: 48000, channels: 2);

        // Prime with 4800 frames
        var data = new float[4800 * 2];
        Array.Fill(data, 0.5f);
        jitter.Write(data);

        // Request 5000 frames (more than available)
        var dest = new float[5000 * 2];
        jitter.Read(dest, 5000);

        jitter.Underflows.ShouldBe(1);
        // The first 4800 frames should be 0.5f, remaining 200 frames should be silence
        dest[0].ShouldBe(0.5f);
        dest[4800 * 2].ShouldBe(0f);
    }
}
