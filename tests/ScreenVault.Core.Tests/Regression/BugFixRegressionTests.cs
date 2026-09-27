using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using NSubstitute;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.Output;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Regression;

/// <summary>
/// Regression tests for bugs fixed in the recording, session and storage code. Paths are built from
/// the temp folder so the tests are rooted on every platform (nothing touches the real disk).
/// </summary>
public sealed class BugFixRegressionTests
{
    private const long OneGb = 1024L * 1024L * 1024L;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sv-regression");

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        public long Timestamp { get; set; } = 1000;

        public double SecondsBetween(long startTimestamp, long endTimestamp) => (endTimestamp - startTimestamp) / 1000.0;

        public void AdvanceSeconds(double seconds)
        {
            Timestamp += (long)(seconds * 1000);
            UtcNow = UtcNow.AddSeconds(seconds);
        }
    }

    private static byte[] VideoPacket(bool keyframe)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = (byte)((keyframe ? 0x40 : 0x00) | ((256 >> 8) & 0x1F));
        packet[2] = 256 & 0xFF;
        packet[3] = 0x30; // adaptation field + payload
        packet[4] = 0x01;
        packet[5] = (byte)(keyframe ? 0x40 : 0x00); // random access indicator
        Array.Fill(packet, (byte)0xCC, 6, 182);
        return packet;
    }

    /// <summary>Stream that accepts a few writes and then fails like a disconnected drive.</summary>
    private sealed class FailingStream(string path, int allowedWrites) : FileSystemStream(new MemoryStream(), path, false)
    {
        private int _writes;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (++_writes > allowedWrites)
            {
                throw new IOException("The device is not ready.");
            }

            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    }

    private static IFileSystem FileSystemFailingUnder(MockFileSystem inner, string failingFolder, int allowedWrites)
    {
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Path.Returns(inner.Path);
        fileSystem.File.Returns(inner.File);
        fileSystem.Directory.Returns(inner.Directory);

        var streams = Substitute.For<IFileStreamFactory>();
        streams.New(Arg.Any<string>(), Arg.Any<FileMode>(), Arg.Any<FileAccess>(), Arg.Any<FileShare>(), Arg.Any<int>())
            .Returns(call =>
            {
                var path = call.ArgAt<string>(0);
                return path.StartsWith(failingFolder, StringComparison.Ordinal)
                    ? new FailingStream(path, allowedWrites)
                    : inner.FileStream.New(path, call.ArgAt<FileMode>(1), call.ArgAt<FileAccess>(2), call.ArgAt<FileShare>(3), call.ArgAt<int>(4));
            });
        fileSystem.FileStream.Returns(streams);
        return fileSystem;
    }

    // ── Segment writer ─────────────────────────────────────────────────────────────

    [Fact]
    public void SegmentWriter_SplitsParts_WhenTheConfiguredDurationIsReached()
    {
        var clock = new FakeClock();
        var closed = new List<SegmentInfo>();
        var writer = new SegmentWriter(_ => P("rec"), onSegmentClosed: closed.Add, fileSystem: new MockFileSystem(), clock: clock);
        writer.ConfigureSplitting(TimeSpan.FromMinutes(10), null);

        writer.BeginStream();
        writer.Write(VideoPacket(keyframe: true));
        clock.AdvanceSeconds(599);
        writer.Write(VideoPacket(keyframe: false));
        closed.ShouldBeEmpty();

        clock.AdvanceSeconds(2);
        writer.Write(VideoPacket(keyframe: false)); // limit reached: split at the next keyframe
        closed.ShouldBeEmpty();

        writer.Write(VideoPacket(keyframe: true));
        closed.Count.ShouldBe(1);
        closed[0].CloseReason.ShouldBe(RotationReason.TimeSplit);
        writer.Current.ShouldNotBeNull();
        writer.Current.Index.ShouldBe(2);

        writer.EndStream();
        closed.Count.ShouldBe(2);
    }

    [Fact]
    public void SegmentWriter_SplitsParts_WhenTheConfiguredSizeIsReached()
    {
        var closed = new List<SegmentInfo>();
        var writer = new SegmentWriter(_ => P("rec"), onSegmentClosed: closed.Add, fileSystem: new MockFileSystem(), clock: new FakeClock());
        writer.ConfigureSplitting(null, 188 * 10);

        writer.BeginStream();
        for (var i = 0; i < 12; i++)
        {
            writer.Write(VideoPacket(keyframe: false));
        }

        closed.ShouldBeEmpty();
        writer.Write(VideoPacket(keyframe: true));
        closed.Count.ShouldBe(1);
        closed[0].CloseReason.ShouldBe(RotationReason.SizeSplit);
        writer.EndStream();
    }

    [Fact]
    public void SegmentWriter_BeginStream_ForgetsTheBytesOfThePreviousRecording()
    {
        var writer = new SegmentWriter(_ => P("rec"), fileSystem: new MockFileSystem(), clock: new FakeClock());
        writer.BeginStream();
        writer.Write(VideoPacket(keyframe: true));
        writer.EndStream();
        writer.BytesWritten.ShouldBe(188);

        writer.BeginStream();

        writer.BytesWritten.ShouldBe(0);
        writer.Current.ShouldBeNull();
        writer.EndStream();
    }

    [Fact]
    public void SegmentWriter_WriteFailure_ReplaysUnflushedDataOnTheNextLocation()
    {
        var mock = new MockFileSystem();
        var fileSystem = FileSystemFailingUnder(mock, P("primary"), allowedWrites: 2);
        var locations = new Queue<string>([P("primary"), P("backup")]);
        var closed = new List<SegmentInfo>();
        var failures = new List<string>();

        var writer = new SegmentWriter(
            _ => locations.Count > 1 ? locations.Dequeue() : locations.Peek(),
            (location, _) => failures.Add(location),
            closed.Add,
            fileSystem,
            new FakeClock());

        writer.BeginStream();
        writer.Write(VideoPacket(keyframe: true));  // reaches the primary drive's buffer
        writer.Write(VideoPacket(keyframe: false)); // reaches the primary drive's buffer
        writer.Write(VideoPacket(keyframe: false)); // the primary drive fails here
        writer.EndStream();

        failures.ShouldBe([P("primary")]);

        // The failed part is still part of the session…
        closed.Count.ShouldBe(2);
        closed[0].CloseReason.ShouldBe(RotationReason.StorageSwitch);
        closed[0].Bytes.ShouldBe(2 * 188);

        // …and nothing is lost: the backup part starts with the data that was not yet flushed.
        closed[1].TsPath.ShouldStartWith(P("backup"));
        mock.FileInfo.New(closed[1].TsPath).Length.ShouldBe(3 * 188);
    }

    // ── Session store and markers ──────────────────────────────────────────────────

    [Fact]
    public void SessionStore_Update_OnALiveSession_ChangesTheRecordersOwnCopy()
    {
        var store = new SessionStore(new MockFileSystem(), P("sessions"));
        var live = new SessionManifest { SessionId = "live", Status = "Recording", StartedAtUtc = DateTime.UtcNow };
        live.Segments.Add(new SegmentManifestEntry { Index = 1, Remux = "Pending" });
        store.RegisterLive(live);
        store.Save(live);

        store.Update("live", m =>
        {
            m.Segments[0].Remux = "Done";
            return true;
        }).ShouldBeTrue();

        // The recorder saves its own copy again later: the remux result must survive that.
        live.Segments[0].Remux.ShouldBe("Done");
        store.Save(live);
        store.Load("live")!.Segments[0].Remux.ShouldBe("Done");

        store.UnregisterLive("live");
        store.IsLive("live").ShouldBeFalse();
    }

    [Fact]
    public void SessionStore_Update_KeepsChangesMadeByOthers()
    {
        var store = new SessionStore(new MockFileSystem(), P("sessions"));
        store.Save(new SessionManifest { SessionId = "done", Status = "Completed", StartedAtUtc = DateTime.UtcNow });

        store.Update("done", m =>
        {
            m.MergedPath = P("rec", "merged.mkv");
            return true;
        });
        store.Update("done", m =>
        {
            m.Title = "Sprint planning";
            return true;
        });

        var loaded = store.Load("done")!;
        loaded.MergedPath.ShouldBe(P("rec", "merged.mkv"));
        loaded.Title.ShouldBe("Sprint planning");
        store.Update("missing", _ => true).ShouldBeFalse();
    }

    [Fact]
    public void MarkerService_UsesRecordedTime_SoPausesDoNotShiftMarkers()
    {
        var service = new MarkerService(new SessionStore(new MockFileSystem(), P("sessions")));
        var manifest = new SessionManifest { SessionId = "m1", StartedAtUtc = DateTime.UtcNow.AddHours(-1) };
        service.SetActiveSession(manifest, null, () => 125.5);

        var marker = service.AddMarker("Demo starts");

        marker.OffsetSec.ShouldBe(125.5);
        manifest.Markers.ShouldHaveSingleItem();
    }

    [Fact]
    public void MarkerService_WithoutARecording_DoesNotReportAMarker()
    {
        var service = new MarkerService(new SessionStore(new MockFileSystem(), P("sessions")));
        var raised = false;
        service.MarkerAdded += (_, _) => raised = true;

        service.AddMarker("Nothing is recording");

        raised.ShouldBeFalse();
        service.HasActiveSession.ShouldBeFalse();
    }

    // ── Storage ────────────────────────────────────────────────────────────────────

    private sealed class FolderProbe : IDiskSpaceProbe
    {
        public Dictionary<string, long> FreeBytes { get; } = new(StringComparer.Ordinal);

        public bool IsReady(string path) => true;

        public long GetAvailableFreeSpace(string path) =>
            FreeBytes.Where(kv => path.StartsWith(kv.Key, StringComparison.Ordinal)).Select(kv => kv.Value).FirstOrDefault(100 * OneGb);

        public bool TestWriteAccess(string path) => true;
    }

    private static StorageSettings Locations(params string[] paths) => new()
    {
        Locations = paths.Select(p => new StorageLocationConfig { Path = p, MinFreeGb = 5, Enabled = true }).ToList()
    };

    [Fact]
    public void StorageManager_UpdateSettings_SendsTheNextPartToTheNewFolder()
    {
        var probe = new FolderProbe();
        using var manager = new StorageManager(Locations(P("old")), probe);
        manager.SelectLocationForNewSegment(OneGb / 10).ShouldBe(P("old"));

        manager.UpdateSettings(Locations(P("new")));

        manager.SelectLocationForNewSegment(OneGb / 10).ShouldBe(P("new"));
    }

    [Fact]
    public void StorageManager_AsksForANewPart_WhenTheActiveDriveRunsLow()
    {
        var probe = new FolderProbe();
        probe.FreeBytes[P("primary")] = 50 * OneGb;
        probe.FreeBytes[P("backup")] = 50 * OneGb;
        var settings = Locations(P("primary"), P("backup"));
        using var manager = new StorageManager(settings, probe);
        manager.SelectLocationForNewSegment(OneGb / 10).ShouldBe(P("primary"));

        var requests = new List<StorageSwitchRequestedEventArgs>();
        manager.SwitchRequested += (_, e) => requests.Add(e);

        probe.FreeBytes[P("primary")] = 2 * OneGb; // below the 5 GB minimum
        manager.UpdateSettings(settings);          // re-probes the drives

        requests.ShouldHaveSingleItem();
        requests[0].RequiresRotation.ShouldBeTrue();
        requests[0].NewLocation.ShouldBe(P("backup"));
        manager.SelectLocationForNewSegment(OneGb / 10).ShouldBe(P("backup"));
    }

    [Fact]
    public async Task Retention_KeepsFoldersWithOtherFiles_AndDeletesTheMergedFile()
    {
        var fs = new MockFileSystem();
        var store = new SessionStore(fs, P("sessions"));
        var settingsService = new SettingsService(P("settings.json"), isPortable: false, fs);
        var settings = settingsService.Current.Clone();
        settings.Storage.Retention.Enabled = true;
        settings.Storage.Retention.KeepDays = 30;
        settingsService.Save(settings);

        var day = P("rec", "2026-01-05");
        var part = Path.Combine(day, "SV_2026-01-05_09-00-00_p001.mkv");
        var merged = Path.Combine(day, "SV_2026-01-05_09-00-00.mkv");
        var userFile = Path.Combine(day, "my notes.txt");
        fs.AddFile(part, new MockFileData("part"));
        fs.AddFile(merged, new MockFileData("merged"));
        fs.AddFile(userFile, new MockFileData("keep me"));

        var manifest = new SessionManifest
        {
            SessionId = "2026-01-05_09-00-00",
            Status = "Completed",
            StartedAtUtc = DateTime.UtcNow.AddDays(-90),
            MergedPath = merged
        };
        manifest.Segments.Add(new SegmentManifestEntry { Index = 1, Location = P("rec"), FinalPath = Path.Combine("2026-01-05", "SV_2026-01-05_09-00-00_p001.mkv") });
        store.Save(manifest);

        using var retention = new RetentionService(store, settingsService, fs);
        (await retention.RunCleanupAsync()).ShouldBe(1);

        fs.File.Exists(part).ShouldBeFalse();
        fs.File.Exists(merged).ShouldBeFalse();
        fs.File.Exists(userFile).ShouldBeTrue();
        fs.Directory.Exists(day).ShouldBeTrue();
    }

    [Fact]
    public async Task Retention_RemovesTheDayFolder_OnceItIsEmpty()
    {
        var fs = new MockFileSystem();
        var store = new SessionStore(fs, P("sessions"));
        var settingsService = new SettingsService(P("settings.json"), isPortable: false, fs);
        var settings = settingsService.Current.Clone();
        settings.Storage.Retention.Enabled = true;
        settingsService.Save(settings);

        var day = P("rec", "2026-01-06");
        var part = Path.Combine(day, "SV_2026-01-06_09-00-00_p001.mkv");
        fs.AddFile(part, new MockFileData("part"));

        var manifest = new SessionManifest { SessionId = "2026-01-06_09-00-00", Status = "Completed", StartedAtUtc = DateTime.UtcNow.AddDays(-90) };
        manifest.Segments.Add(new SegmentManifestEntry { Index = 1, Location = P("rec"), FinalPath = part });
        store.Save(manifest);

        using var retention = new RetentionService(store, settingsService, fs);
        await retention.RunCleanupAsync();

        fs.Directory.Exists(day).ShouldBeFalse();
    }

    // ── Settings ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SettingsService_IsFirstRun_OnlyWhenNoSettingsExistedYet()
    {
        var fs = new MockFileSystem();
        new SettingsService(P("first", "settings.json"), isPortable: false, fs).IsFirstRun.ShouldBeTrue();
        new SettingsService(P("first", "settings.json"), isPortable: false, fs).IsFirstRun.ShouldBeFalse();
    }
}
