using ScreenVault.Core.Output;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Output;

public sealed class TsPacketAlignerTests
{
    private static byte[] CreateDummyPacket(byte payloadByte = 0xAA)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x00;
        packet[2] = 0x10;
        packet[3] = 0x10;
        Array.Fill(packet, payloadByte, 4, 184);
        return packet;
    }

    [Fact]
    public void Feed_ExactPackets_YieldsAllPackets()
    {
        var aligner = new TsPacketAligner();
        var p1 = CreateDummyPacket(1);
        var p2 = CreateDummyPacket(2);
        var combined = p1.Concat(p2).ToArray();

        var packets = new List<byte[]>();
        aligner.Feed(combined, pkt => packets.Add(pkt.ToArray()));

        packets.Count.ShouldBe(2);
        packets[0][0].ShouldBe((byte)0x47);
        packets[0][4].ShouldBe((byte)1);
        packets[1][4].ShouldBe((byte)2);
    }

    [Fact]
    public void Feed_FragmentedChunks_ReassemblesPacketsAcrossCalls()
    {
        var aligner = new TsPacketAligner();
        var p1 = CreateDummyPacket(10);
        var p2 = CreateDummyPacket(20);
        var stream = p1.Concat(p2).ToArray();

        var packets = new List<byte[]>();

        // Feed in 50-byte chunks
        var offset = 0;
        while (offset < stream.Length)
        {
            var take = Math.Min(50, stream.Length - offset);
            aligner.Feed(stream.AsSpan(offset, take), pkt => packets.Add(pkt.ToArray()));
            offset += take;
        }

        packets.Count.ShouldBe(2);
        packets[0][4].ShouldBe((byte)10);
        packets[1][4].ShouldBe((byte)20);
    }

    [Fact]
    public void Feed_LeadingGarbage_ResynchronizesToNextSyncByte()
    {
        var aligner = new TsPacketAligner();
        var garbage = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var p1 = CreateDummyPacket(99);
        var stream = garbage.Concat(p1).ToArray();

        var packets = new List<byte[]>();
        aligner.Feed(stream, pkt => packets.Add(pkt.ToArray()));

        packets.Count.ShouldBe(1);
        packets[0][4].ShouldBe((byte)99);
    }

    [Fact]
    public void Feed_SampleStream_RandomChunkSizes_ProducesByteIdenticalOutput()
    {
        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        Directory.CreateDirectory(fixturesDir);
        var fixturePath = Path.Combine(fixturesDir, "sample_20s.ts");

        // Generate deterministic 20s equivalent sample TS stream if not on disk
        if (!File.Exists(fixturePath))
        {
            using var fs = File.Create(fixturePath);
            var rand = new Random(12345);
            // 500 packets of 188 bytes
            for (var i = 0; i < 500; i++)
            {
                var packet = new byte[188];
                packet[0] = 0x47;
                packet[1] = (byte)(i % 2 == 0 ? 0x01 : 0x00);
                packet[2] = (byte)(i & 0xFF);
                packet[3] = (byte)(0x10 | (i & 0x0F));
                rand.NextBytes(packet.AsSpan(4, 184));
                fs.Write(packet);
            }
        }

        var sourceBytes = File.ReadAllBytes(fixturePath);
        var aligner = new TsPacketAligner();
        var reconstructed = new MemoryStream();

        var random = new Random(42);
        var offset = 0;
        while (offset < sourceBytes.Length)
        {
            var chunkSize = random.Next(1, 4096);
            var count = Math.Min(chunkSize, sourceBytes.Length - offset);
            aligner.Feed(sourceBytes.AsSpan(offset, count), pkt => reconstructed.Write(pkt));
            offset += count;
        }

        var outputBytes = reconstructed.ToArray();
        outputBytes.Length.ShouldBe(sourceBytes.Length);
        outputBytes.ShouldBe(sourceBytes);
    }
}
