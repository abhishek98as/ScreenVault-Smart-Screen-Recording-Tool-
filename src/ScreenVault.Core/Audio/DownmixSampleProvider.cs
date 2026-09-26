namespace ScreenVault.Core.Audio;

public static class DownmixHelper
{
    private const float CenterScale = 0.70710678f;

    public static void DownmixToStereo(ReadOnlySpan<float> input, int inputChannels, Span<float> outputStereo, int frames)
    {
        if (inputChannels == 1)
        {
            // Mono to stereo
            for (var i = 0; i < frames; i++)
            {
                var sample = input[i];
                outputStereo[i * 2] = sample;
                outputStereo[i * 2 + 1] = sample;
            }
        }
        else if (inputChannels == 2)
        {
            // Stereo direct copy
            input[..(frames * 2)].CopyTo(outputStereo[..(frames * 2)]);
        }
        else if (inputChannels >= 6)
        {
            // 5.1 (FL, FR, C, LFE, SL, SR) or 7.1 (+ BL, BR)
            // Scale by 0.5 to avoid clipping when summing channels
            const float norm = 0.5f;
            for (var i = 0; i < frames; i++)
            {
                var baseIdx = i * inputChannels;
                var fl = input[baseIdx];
                var fr = input[baseIdx + 1];
                var c = input[baseIdx + 2];
                var sl = input[baseIdx + 4];
                var sr = input[baseIdx + 5];

                var left = fl + c * CenterScale + sl * CenterScale;
                var right = fr + c * CenterScale + sr * CenterScale;

                if (inputChannels >= 8)
                {
                    var bl = input[baseIdx + 6];
                    var br = input[baseIdx + 7];
                    left += bl * CenterScale;
                    right += br * CenterScale;
                }

                outputStereo[i * 2] = left * norm;
                outputStereo[i * 2 + 1] = right * norm;
            }
        }
        else
        {
            // Fallback for 3 or 4 channels: take first two
            for (var i = 0; i < frames; i++)
            {
                outputStereo[i * 2] = input[i * inputChannels];
                outputStereo[i * 2 + 1] = input[i * inputChannels + 1];
            }
        }
    }
}
