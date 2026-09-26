namespace ScreenVault.Core.Output;

public readonly ref struct TsHeader
{
    public const int PacketSize = 188;
    public const byte SyncByte = 0x47;

    public bool HasSyncByte { get; }
    public bool TransportError { get; }
    public bool PayloadUnitStartIndicator { get; }
    public int Pid { get; }
    public byte AdaptationFieldControl { get; }
    public byte ContinuityCounter { get; }
    public bool HasAdaptationField { get; }
    public bool HasPayload { get; }
    public bool RandomAccessIndicator { get; }

    public TsHeader(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PacketSize)
        {
            HasSyncByte = false;
            return;
        }

        HasSyncByte = packet[0] == SyncByte;
        if (!HasSyncByte)
        {
            return;
        }

        TransportError = (packet[1] & 0x80) != 0;
        PayloadUnitStartIndicator = (packet[1] & 0x40) != 0;
        Pid = ((packet[1] & 0x1F) << 8) | packet[2];

        AdaptationFieldControl = (byte)((packet[3] >> 4) & 0x03);
        ContinuityCounter = (byte)(packet[3] & 0x0F);

        HasPayload = (AdaptationFieldControl & 0x01) != 0;
        HasAdaptationField = (AdaptationFieldControl & 0x02) != 0;

        if (HasAdaptationField && packet.Length > 5)
        {
            var adaptationFieldLength = packet[4];
            if (adaptationFieldLength > 0)
            {
                // Bit 6 in byte 5 is random_access_indicator (RAI)
                RandomAccessIndicator = (packet[5] & 0x40) != 0;
            }
        }
    }
}
