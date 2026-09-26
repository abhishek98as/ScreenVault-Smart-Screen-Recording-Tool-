namespace ScreenVault.Core.Audio;

public sealed class JitterBuffer
{
    private enum BufferState
    {
        Priming,
        Playing
    }

    private readonly FloatRingBuffer _ring;
    private readonly int _targetFloats;
    private readonly int _highFloats;
    private BufferState _state = BufferState.Priming;

    public long Underflows { get; private set; }
    public long DriftTrims { get; private set; }

    public int AvailableFrames => _ring.AvailableFloats / 2;

    public JitterBuffer(int targetMs = 100, int sampleRate = 48000, int channels = 2)
    {
        targetMs = Math.Clamp(targetMs, 30, 500);
        _targetFloats = (int)(sampleRate * (targetMs / 1000.0) * channels);
        _highFloats = _targetFloats * 3; // 3x target is high watermark (~300 ms)

        var capacityFloats = sampleRate * 2 * channels; // 2 seconds capacity
        _ring = new FloatRingBuffer(capacityFloats);
    }

    public void Write(ReadOnlySpan<float> source)
    {
        _ring.Write(source);
    }

    public void Read(Span<float> destination, int frames)
    {
        var neededFloats = frames * 2;
        if (destination.Length < neededFloats)
        {
            throw new ArgumentException("Destination span too small for requested frames", nameof(destination));
        }

        var availFloats = _ring.AvailableFloats;

        if (_state == BufferState.Priming)
        {
            if (availFloats < _targetFloats)
            {
                destination[..neededFloats].Clear();
                return;
            }
            _state = BufferState.Playing;
        }

        var toTakeFloats = Math.Min(availFloats, neededFloats);
        _ring.Read(destination[..toTakeFloats]);

        if (toTakeFloats < neededFloats)
        {
            // Underflow: pad remainder with silence, return to priming
            destination[toTakeFloats..neededFloats].Clear();
            _state = BufferState.Priming;
            Underflows++;
        }

        // Drift control AFTER satisfying the read
        var remainingFloats = _ring.AvailableFloats;
        if (remainingFloats > _highFloats)
        {
            var dropFloats = remainingFloats - _targetFloats;
            _ring.Skip(dropFloats);
            DriftTrims++;
        }
    }

    public void Reset()
    {
        _ring.Clear();
        _state = BufferState.Priming;
    }
}
