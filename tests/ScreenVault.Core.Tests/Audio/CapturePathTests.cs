using NAudio.Wave;
using ScreenVault.Core.Audio;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Audio;

/// <summary>
/// Drives the real capture-side code (format conversion → jitter buffer → mixer) with the kinds of
/// buffers WASAPI delivers, and checks that sound actually comes out the other end.
/// </summary>
public class CapturePathTests
{
    public static TheoryData<int, int> DeviceFormats => new()
    {
        { 48000, 2 },  // typical speaker loopback
        { 44100, 2 },  // resampled
        { 48000, 4 },  // laptop microphone array
        { 48000, 1 },  // mono USB microphone
        { 16000, 1 },  // headset (hands-free profile)
    };

    [Theory]
    [MemberData(nameof(DeviceFormats))]
    public void FloatDevice_SineIsAudibleAfterMixing(int sampleRate, int channels)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        var output = RunPipeline(format, (buffer, index, channel, frame) =>
        {
            var sample = 0.5f * MathF.Sin(2 * MathF.PI * 440f * frame / sampleRate);
            BitConverter.TryWriteBytes(buffer.AsSpan(((index * channels) + channel) * 4, 4), sample);
        });

        Rms(output).ShouldBeGreaterThan(0.1f);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void PcmDevice_SineIsAudibleAfterMixing(int bits)
    {
        const int sampleRate = 48000;
        const int channels = 2;
        var format = new WaveFormat(sampleRate, bits, channels);
        var bytesPerSample = bits / 8;
        var output = RunPipeline(format, (buffer, index, channel, frame) =>
        {
            var value = (int)(0.5 * Math.Sin(2 * Math.PI * 440 * frame / sampleRate) * ((1 << (bits - 1)) - 1));
            var offset = ((index * channels) + channel) * bytesPerSample;
            for (var b = 0; b < bytesPerSample; b++)
            {
                buffer[offset + b] = (byte)(value >> (8 * b));
            }
        });

        Rms(output).ShouldBeGreaterThan(0.1f);
    }

    /// <summary>Simulates 2 s of 10 ms capture callbacks interleaved with 10 ms pump reads.</summary>
    private static float[] RunPipeline(WaveFormat format, Action<byte[], int, int, int> writeSample)
    {
        var pipeline = new FormatPipeline(format);
        var jitter = new JitterBuffer(100);
        var meter = new LevelMeter();
        var framesPerCallback = format.SampleRate / 100;
        var captured = new byte[framesPerCallback * format.BlockAlign];
        var output = new List<float>();
        var scratch = new float[480 * 2];
        var mixed = new float[480 * 2];
        var frame = 0;

        for (var tick = 0; tick < 200; tick++)
        {
            for (var f = 0; f < framesPerCallback; f++, frame++)
            {
                for (var c = 0; c < format.Channels; c++)
                {
                    writeSample(captured, f, c, frame);
                }
            }

            pipeline.PushData(captured, 0, captured.Length, jitter, meter);

            Array.Clear(mixed);
            jitter.Read(scratch, 480);
            Mixer.Accumulate(mixed, scratch, 480, 1.0f);
            SoftLimiter.Process(mixed, 480);
            if (tick >= 50)
            {
                output.AddRange(mixed);
            }
        }

        meter.PeakDb.ShouldBeGreaterThan(-20f, "the level meter should see the signal");
        return [.. output];
    }

    private static float Rms(float[] samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / Math.Max(1, samples.Length));
    }
}
