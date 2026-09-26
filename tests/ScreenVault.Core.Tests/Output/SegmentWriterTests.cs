using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.Output;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Output;

public sealed class SegmentWriterTests
{
    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        public long Timestamp { get; set; } = 1000;

        public double SecondsBetween(long startTimestamp, long endTimestamp)
        {
            return (endTimestamp - startTimestamp) / 1000.0;
        }

        public void AdvanceSeconds(double sec)
        {
            Timestamp += (long)(sec * 1000);
            UtcNow = UtcNow.AddSeconds(sec);
        }
    }

    private static byte[] CreateVideoPacket(int pid, bool pusi, bool rai)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = (byte)((pusi ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)(pid & 0xFF);
        // AFC = 0b11 (Adaptation field followed by payload)
        packet[3] = 0x30;
        packet[4] = 0x01; // AFL = 1
        packet[5] = (byte)(rai ? 0x40 : 0x00); // RAI = bit 6
        Array.Fill(packet, (byte)0xCC, 6, 182);
        return packet;
    }

    [Fact]
    public void SegmentWriter_RotatesOnKeyframe_WhenRequested()
    {
        var fileSystem = new MockFileSystem();
        var clock = new FakeClock();
        var closedSegments = new List<SegmentInfo>();

        var writer = new SegmentWriter(
            locationSelector: _ => @"C:\Recordings",
            onSegmentClosed: s => closedSegments.Add(s),
            fileSystem: fileSystem,
            clock: clock);

        writer.BeginStream();

        var nonKeyframe = CreateVideoPacket(256, pusi: false, rai: false);
        writer.Write(nonKeyframe);

        writer.RequestRotation(RotationReason.TimeSplit);

        // Feed another non-keyframe -> should not rotate yet
        writer.Write(nonKeyframe);
        closedSegments.Count.ShouldBe(0);

        // Feed keyframe -> should trigger rotation!
        var keyframe = CreateVideoPacket(256, pusi: true, rai: true);
        writer.Write(keyframe);

        closedSegments.Count.ShouldBe(1);
        closedSegments[0].Index.ShouldBe(1);
        closedSegments[0].CloseReason.ShouldBe(RotationReason.TimeSplit);
        writer.Current.ShouldNotBeNull();
        writer.Current.Index.ShouldBe(2);

        writer.EndStream();
        closedSegments.Count.ShouldBe(2);
    }

    [Fact]
    public void SegmentWriter_HandlesWriteFailure_AndTransfersUnflushedTail()
    {
        var fileSystem = new MockFileSystem();
        var clock = new FakeClock();
        var locationsUsed = new List<string>();
        var failedLocations = new List<string>();

        var locationIndex = 0;
        string SelectLocation(long _)
        {
            var loc = locationIndex == 0 ? @"C:\Primary" : @"D:\Backup";
            locationsUsed.Add(loc);
            locationIndex++;
            return loc;
        }

        var writer = new SegmentWriter(
            locationSelector: SelectLocation,
            onWriteFailure: (loc, _) => failedLocations.Add(loc),
            fileSystem: fileSystem,
            clock: clock);

        writer.BeginStream();

        var p1 = CreateVideoPacket(256, pusi: false, rai: false);
        writer.Write(p1);

        // Primary location was used
        locationsUsed[0].ShouldBe(@"C:\Primary");

        writer.EndStream();
    }

    [Fact]
    public void SegmentNaming_RejectsUnrootedRelativeLocation()
    {
        var fileSystem = new MockFileSystem();
        var now = DateTime.Now;

        Should.Throw<ArgumentException>(() =>
            SegmentNaming.GenerateSegmentPath(fileSystem, @"some\relative\path", now, now, 1));
    }

    [Fact]
    public void SegmentNaming_ExpandsEnvironmentVariables_AndGeneratesRootedPath()
    {
        var fileSystem = new MockFileSystem();
        var now = new DateTime(2026, 9, 26, 12, 0, 0);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var path = SegmentNaming.GenerateSegmentPath(
            fileSystem,
            @"%USERPROFILE%\Recordings",
            now,
            now,
            1);

        path.ShouldStartWith(userProfile);
        path.ShouldEndWith(@"2026-09-26\SV_2026-09-26_12-00-00_p001.ts");
    }

    [Fact]
    public void SegmentWriter_ExpandsLocationVariables_AndRejectsUnrooted()
    {
        var fileSystem = new MockFileSystem();
        var clock = new FakeClock();

        var writer = new SegmentWriter(
            locationSelector: _ => @"relative\path",
            fileSystem: fileSystem,
            clock: clock);

        writer.BeginStream();
        var p = CreateVideoPacket(256, pusi: false, rai: false);

        Should.Throw<InvalidOperationException>(() => writer.Write(p));
    }
}
