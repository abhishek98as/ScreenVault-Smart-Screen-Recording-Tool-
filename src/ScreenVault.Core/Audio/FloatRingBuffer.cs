namespace ScreenVault.Core.Audio;

public sealed class FloatRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private volatile int _writePos;
    private volatile int _readPos;

    public int Capacity => _capacity;

    public int AvailableFloats
    {
        get
        {
            var diff = _writePos - _readPos;
            return diff < 0 ? diff + _capacity : diff;
        }
    }

    public int FreeFloats => (_capacity - 1) - AvailableFloats;

    public FloatRingBuffer(int capacityFloats)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFloats);
        _capacity = capacityFloats + 1; // 1 extra for full/empty distinction
        _buffer = new float[_capacity];
    }

    public int Write(ReadOnlySpan<float> source)
    {
        var toWrite = Math.Min(source.Length, FreeFloats);
        if (toWrite <= 0) return 0;

        var firstChunk = Math.Min(toWrite, _capacity - _writePos);
        source[..firstChunk].CopyTo(_buffer.AsSpan(_writePos, firstChunk));

        var secondChunk = toWrite - firstChunk;
        if (secondChunk > 0)
        {
            source.Slice(firstChunk, secondChunk).CopyTo(_buffer.AsSpan(0, secondChunk));
            _writePos = secondChunk;
        }
        else
        {
            _writePos = (_writePos + firstChunk) % _capacity;
        }

        return toWrite;
    }

    public int Read(Span<float> destination)
    {
        var toRead = Math.Min(destination.Length, AvailableFloats);
        if (toRead <= 0) return 0;

        var firstChunk = Math.Min(toRead, _capacity - _readPos);
        _buffer.AsSpan(_readPos, firstChunk).CopyTo(destination[..firstChunk]);

        var secondChunk = toRead - firstChunk;
        if (secondChunk > 0)
        {
            _buffer.AsSpan(0, secondChunk).CopyTo(destination.Slice(firstChunk, secondChunk));
            _readPos = secondChunk;
        }
        else
        {
            _readPos = (_readPos + firstChunk) % _capacity;
        }

        return toRead;
    }

    public void Skip(int countFloats)
    {
        var toSkip = Math.Min(countFloats, AvailableFloats);
        if (toSkip > 0)
        {
            _readPos = (_readPos + toSkip) % _capacity;
        }
    }

    public void Clear()
    {
        _readPos = 0;
        _writePos = 0;
    }
}
