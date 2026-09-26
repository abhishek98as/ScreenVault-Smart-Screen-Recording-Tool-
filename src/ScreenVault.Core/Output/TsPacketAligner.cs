namespace ScreenVault.Core.Output;

public delegate void TsPacketCallback(ReadOnlySpan<byte> packet);

public sealed class TsPacketAligner
{
    public const int PacketSize = TsHeader.PacketSize;
    public const byte SyncByte = TsHeader.SyncByte;

    private readonly byte[] _carryOverBuffer = new byte[PacketSize * 4];
    private int _carryOverLength;

    public void Reset()
    {
        _carryOverLength = 0;
    }

    public void Feed(ReadOnlySpan<byte> data, TsPacketCallback onPacket)
    {
        if (data.IsEmpty) return;

        // If we have carry-over bytes from previous feed
        if (_carryOverLength > 0)
        {
            var needed = PacketSize - _carryOverLength;
            if (data.Length < needed)
            {
                data.CopyTo(_carryOverBuffer.AsSpan(_carryOverLength));
                _carryOverLength += data.Length;
                return;
            }

            data[..needed].CopyTo(_carryOverBuffer.AsSpan(_carryOverLength));
            data = data[needed..];
            _carryOverLength = 0;

            if (_carryOverBuffer[0] == SyncByte)
            {
                onPacket(_carryOverBuffer.AsSpan(0, PacketSize));
            }
            else
            {
                // Sync lost in carry over, resynchronize
                Resynchronize(_carryOverBuffer.AsSpan(0, PacketSize), onPacket);
            }
        }

        // Process full packets from data span
        var offset = 0;
        while (offset + PacketSize <= data.Length)
        {
            if (data[offset] == SyncByte)
            {
                onPacket(data.Slice(offset, PacketSize));
                offset += PacketSize;
            }
            else
            {
                // Loss of sync, find next 0x47
                var nextSync = FindNextValidSync(data[offset..]);
                if (nextSync >= 0)
                {
                    offset += nextSync;
                }
                else
                {
                    // No sync byte found in remainder
                    offset = data.Length;
                    break;
                }
            }
        }

        // Store remainder in carry-over buffer
        var remaining = data.Length - offset;
        if (remaining > 0)
        {
            data.Slice(offset, remaining).CopyTo(_carryOverBuffer.AsSpan(0));
            _carryOverLength = remaining;
        }
    }

    private void Resynchronize(ReadOnlySpan<byte> span, TsPacketCallback onPacket)
    {
        var syncIdx = FindNextValidSync(span);
        if (syncIdx >= 0)
        {
            var len = span.Length - syncIdx;
            span.Slice(syncIdx, len).CopyTo(_carryOverBuffer.AsSpan(0));
            _carryOverLength = len;
        }
    }

    private static int FindNextValidSync(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == SyncByte)
            {
                // Verify repetition at 188 intervals if data is long enough
                var valid = true;
                for (var check = i + PacketSize; check < data.Length && check <= i + PacketSize * 3; check += PacketSize)
                {
                    if (data[check] != SyncByte)
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid)
                {
                    return i;
                }
            }
        }

        return -1;
    }
}
