using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ScreenVault.Core.Audio;

public sealed class FormatPipeline
{
    private sealed class ChannelMapperSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private readonly WaveFormat _waveFormat;
        private readonly float[] _sourceBuffer;

        public WaveFormat WaveFormat => _waveFormat;

        public ChannelMapperSampleProvider(ISampleProvider source)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
            _sourceBuffer = new float[4096 * _sourceChannels];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var requestedFrames = count / 2;
            var framesToRead = Math.Min(requestedFrames, _sourceBuffer.Length / _sourceChannels);
            if (framesToRead <= 0) return 0;

            var samplesRead = _source.Read(_sourceBuffer, 0, framesToRead * _sourceChannels);
            if (samplesRead <= 0) return 0;

            var framesRead = samplesRead / _sourceChannels;
            DownmixHelper.DownmixToStereo(
                _sourceBuffer.AsSpan(0, samplesRead),
                _sourceChannels,
                buffer.AsSpan(offset, framesRead * 2),
                framesRead);

            return framesRead * 2;
        }
    }

    private readonly BufferedWaveProvider _bufferedWaveProvider;
    private readonly ISampleProvider _outputPipeline;
    private readonly float[] _drainBuffer = new float[4096];

    public FormatPipeline(WaveFormat deviceFormat)
    {
        _bufferedWaveProvider = new BufferedWaveProvider(deviceFormat)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };

        var sampleProvider = _bufferedWaveProvider.ToSampleProvider();
        if (deviceFormat.Channels != 2)
        {
            sampleProvider = new ChannelMapperSampleProvider(sampleProvider);
        }

        if (sampleProvider.WaveFormat.SampleRate != 48000)
        {
            _outputPipeline = new WdlResamplingSampleProvider(sampleProvider, 48000);
        }
        else
        {
            _outputPipeline = sampleProvider;
        }
    }

    public void PushData(byte[] buffer, int offset, int count, JitterBuffer jitterBuffer, LevelMeter? meter = null)
    {
        if (count <= 0) return;

        _bufferedWaveProvider.AddSamples(buffer, offset, count);

        int read;
        while ((read = _outputPipeline.Read(_drainBuffer, 0, _drainBuffer.Length)) > 0)
        {
            meter?.Update(_drainBuffer.AsSpan(0, read), read / 2);
            jitterBuffer.Write(_drainBuffer.AsSpan(0, read));
        }
    }
}
