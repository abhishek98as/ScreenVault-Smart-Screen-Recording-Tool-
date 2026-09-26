namespace ScreenVault.Core.Audio;

public static class SoftLimiter
{
    private const float Threshold = 0.8912509f; // -1 dBFS
    private const float Headroom = 1.0f - Threshold;

    public static void Process(Span<float> buffer, int frames)
    {
        var totalFloats = frames * 2;
        for (var i = 0; i < totalFloats; i++)
        {
            var sample = buffer[i];
            var abs = MathF.Abs(sample);

            if (abs > Threshold)
            {
                var sign = MathF.Sign(sample);
                var excess = (abs - Threshold) / Headroom;
                var compressed = Threshold + Headroom * MathF.Tanh(excess);
                buffer[i] = sign * Math.Clamp(compressed, -1.0f, 1.0f);
            }
        }
    }
}
