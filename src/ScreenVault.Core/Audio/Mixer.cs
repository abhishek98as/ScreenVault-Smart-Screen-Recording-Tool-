namespace ScreenVault.Core.Audio;

public static class Mixer
{
    public static float DbToLinear(float db)
    {
        if (db <= -60f) return 0f;
        return MathF.Pow(10f, db / 20f);
    }

    public static void Accumulate(Span<float> destination, ReadOnlySpan<float> source, int frames, float gainLinear)
    {
        var totalFloats = frames * 2;
        if (gainLinear <= 0f) return;

        if (Math.Abs(gainLinear - 1.0f) < 0.0001f)
        {
            for (var i = 0; i < totalFloats; i++)
            {
                destination[i] += source[i];
            }
        }
        else
        {
            for (var i = 0; i < totalFloats; i++)
            {
                destination[i] += source[i] * gainLinear;
            }
        }
    }
}
