using ScreenVault.Core.Output;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Output;

public sealed class TsPsiCacheTests
{
    private static byte[] CreateSamplePatPacket(int pmtPid = 0x1000)
    {
        var packet = new byte[188];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47; // Sync
        packet[1] = 0x40; // PUSI = 1, PID = 0 (PAT)
        packet[2] = 0x00;
        packet[3] = 0x10; // Payload only, CC = 0
        packet[4] = 0x00; // Pointer field = 0

        var offset = 5;
        packet[offset + 0] = 0x00; // Table ID = PAT
        packet[offset + 1] = 0xB0; // section syntax + length
        packet[offset + 2] = 0x0D; // length = 13
        packet[offset + 3] = 0x00; // ts id
        packet[offset + 4] = 0x01;
        packet[offset + 5] = 0xC1; // version, cni
        packet[offset + 6] = 0x00; // sec num
        packet[offset + 7] = 0x00; // last sec num

        // Program 1 -> PMT PID
        packet[offset + 8] = 0x00;
        packet[offset + 9] = 0x01; // Program 1
        packet[offset + 10] = (byte)(0xE0 | ((pmtPid >> 8) & 0x1F));
        packet[offset + 11] = (byte)(pmtPid & 0xFF);

        return packet;
    }

    private static byte[] CreateSamplePmtPacket(int pmtPid = 0x1000, int videoPid = 0x0100, int audioPid = 0x0101)
    {
        var packet = new byte[188];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | ((pmtPid >> 8) & 0x1F));
        packet[2] = (byte)(pmtPid & 0xFF);
        packet[3] = 0x10;
        packet[4] = 0x00; // pointer

        var offset = 5;
        packet[offset + 0] = 0x02; // Table ID = PMT
        packet[offset + 1] = 0xB0;
        packet[offset + 2] = 0x17; // length = 23
        packet[offset + 3] = 0x00;
        packet[offset + 4] = 0x01;
        packet[offset + 5] = 0xC1;
        packet[offset + 6] = 0x00;
        packet[offset + 7] = 0x00;
        packet[offset + 8] = (byte)(0xE0 | ((videoPid >> 8) & 0x1F)); // PCR PID
        packet[offset + 9] = (byte)(videoPid & 0xFF);
        packet[offset + 10] = 0xF0;
        packet[offset + 11] = 0x00; // prog info length = 0

        // ES 1: Video H.264 (0x1B)
        packet[offset + 12] = TsPsiCache.StreamTypeH264;
        packet[offset + 13] = (byte)(0xE0 | ((videoPid >> 8) & 0x1F));
        packet[offset + 14] = (byte)(videoPid & 0xFF);
        packet[offset + 15] = 0xF0;
        packet[offset + 16] = 0x00;

        // ES 2: Audio AAC (0x0F)
        packet[offset + 17] = TsPsiCache.StreamTypeAac;
        packet[offset + 18] = (byte)(0xE0 | ((audioPid >> 8) & 0x1F));
        packet[offset + 19] = (byte)(audioPid & 0xFF);
        packet[offset + 20] = 0xF0;
        packet[offset + 21] = 0x00;

        return packet;
    }

    [Fact]
    public void ProcessPacket_ExtractsPmtVideoAndAudioPids()
    {
        var cache = new TsPsiCache();
        var pat = CreateSamplePatPacket(pmtPid: 4096);
        var pmt = CreateSamplePmtPacket(pmtPid: 4096, videoPid: 256, audioPid: 257);

        cache.ProcessPacket(pat, new TsHeader(pat));
        cache.PmtPid.ShouldBe(4096);

        cache.ProcessPacket(pmt, new TsHeader(pmt));
        cache.VideoPid.ShouldBe(256);
        cache.AudioPid.ShouldBe(257);
        cache.HasCompletePsi.ShouldBeTrue();
    }
}
