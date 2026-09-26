using Serilog;

namespace ScreenVault.Core.Output;

public sealed class TsPsiCache
{
    private const int PatPid = 0;
    private const byte PatTableId = 0x00;
    private const byte PmtTableId = 0x02;

    public const byte StreamTypeH264 = 0x1B;
    public const byte StreamTypeAac = 0x0F;

    public int PmtPid { get; private set; } = -1;
    public int VideoPid { get; private set; } = -1;
    public int AudioPid { get; private set; } = -1;

    public byte[]? CachedPatPacket { get; private set; }
    public byte[]? CachedPmtPacket { get; private set; }

    public bool HasCompletePsi => CachedPatPacket != null && CachedPmtPacket != null && VideoPid != -1;

    public void Reset()
    {
        PmtPid = -1;
        VideoPid = -1;
        AudioPid = -1;
        CachedPatPacket = null;
        CachedPmtPacket = null;
    }

    public void ProcessPacket(ReadOnlySpan<byte> packet, in TsHeader header)
    {
        if (header.Pid == PatPid)
        {
            ParsePat(packet, in header);
        }
        else if (header.Pid == PmtPid && PmtPid != -1)
        {
            ParsePmt(packet, in header);
        }
    }

    private void ParsePat(ReadOnlySpan<byte> packet, in TsHeader header)
    {
        CachedPatPacket = packet.ToArray();

        var payloadOffset = GetPayloadOffset(packet, in header);
        if (payloadOffset < 0 || payloadOffset >= packet.Length) return;

        // Pointer field if PUSI == 1
        var offset = payloadOffset;
        if (header.PayloadUnitStartIndicator)
        {
            var pointerField = packet[offset];
            offset += 1 + pointerField;
        }

        if (offset + 8 >= packet.Length) return;

        var tableId = packet[offset];
        if (tableId != PatTableId) return;

        var sectionLength = ((packet[offset + 1] & 0x0F) << 8) | packet[offset + 2];
        var end = offset + 3 + sectionLength - 4; // exclude 4-byte CRC

        offset += 8; // skip table_id, section_len, transport_stream_id, version/cni, section_number, last_section_number

        while (offset + 4 <= end && offset + 4 <= packet.Length)
        {
            var programNum = (packet[offset] << 8) | packet[offset + 1];
            var pmtOrNitPid = ((packet[offset + 2] & 0x1F) << 8) | packet[offset + 3];

            if (programNum != 0) // program 0 is Network Information Table
            {
                PmtPid = pmtOrNitPid;
                Log.Debug("Parsed PAT: Found PMT PID {PmtPid} for program {ProgramNum}", PmtPid, programNum);
                break;
            }

            offset += 4;
        }
    }

    private void ParsePmt(ReadOnlySpan<byte> packet, in TsHeader header)
    {
        CachedPmtPacket = packet.ToArray();

        var payloadOffset = GetPayloadOffset(packet, in header);
        if (payloadOffset < 0 || payloadOffset >= packet.Length) return;

        var offset = payloadOffset;
        if (header.PayloadUnitStartIndicator)
        {
            var pointerField = packet[offset];
            offset += 1 + pointerField;
        }

        if (offset + 12 >= packet.Length) return;

        var tableId = packet[offset];
        if (tableId != PmtTableId) return;

        var sectionLength = ((packet[offset + 1] & 0x0F) << 8) | packet[offset + 2];
        var end = offset + 3 + sectionLength - 4; // exclude CRC

        var programInfoLength = ((packet[offset + 10] & 0x0F) << 8) | packet[offset + 11];
        offset += 12 + programInfoLength;

        while (offset + 5 <= end && offset + 5 <= packet.Length)
        {
            var streamType = packet[offset];
            var elementaryPid = ((packet[offset + 1] & 0x1F) << 8) | packet[offset + 2];
            var esInfoLength = ((packet[offset + 3] & 0x0F) << 8) | packet[offset + 4];

            if (streamType == StreamTypeH264)
            {
                VideoPid = elementaryPid;
                Log.Debug("Parsed PMT: Found H.264 Video PID {VideoPid}", VideoPid);
            }
            else if (streamType == StreamTypeAac)
            {
                AudioPid = elementaryPid;
                Log.Debug("Parsed PMT: Found AAC Audio PID {AudioPid}", AudioPid);
            }

            offset += 5 + esInfoLength;
        }
    }

    private static int GetPayloadOffset(ReadOnlySpan<byte> packet, in TsHeader header)
    {
        var offset = 4;
        if (header.HasAdaptationField)
        {
            if (packet.Length <= 4) return -1;
            var afLength = packet[4];
            offset += 1 + afLength;
        }
        return offset;
    }
}
