using ScreenVault.Core.Audio;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Audio;

public sealed class MixerAndLimiterTests
{
    [Fact]
    public void Mixer_AccumulatesWithGain()
    {
        var dest = new float[4];
        var src = new float[] { 1.0f, -1.0f, 0.5f, -0.5f };

        Mixer.Accumulate(dest, src, frames: 2, gainLinear: 0.5f);

        dest[0].ShouldBe(0.5f);
        dest[1].ShouldBe(-0.5f);
        dest[2].ShouldBe(0.25f);
        dest[3].ShouldBe(-0.25f);
    }

    [Fact]
    public void SoftLimiter_CompresessPeaks_WithoutExceedingOne()
    {
        var buffer = new float[] { 0.5f, -0.5f, 1.5f, -2.0f, 10.0f, -100.0f };

        SoftLimiter.Process(buffer, frames: 3);

        // Sub-threshold values are untouched
        buffer[0].ShouldBe(0.5f);
        buffer[1].ShouldBe(-0.5f);

        // Peak values are compressed into [-1.0, 1.0]
        foreach (var sample in buffer)
        {
            sample.ShouldBeInRange(-1.0f, 1.0f);
        }
    }
}
