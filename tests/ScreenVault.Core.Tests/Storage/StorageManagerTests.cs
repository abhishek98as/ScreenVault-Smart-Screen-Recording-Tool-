using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Xunit;

namespace ScreenVault.Core.Tests.Storage;

public sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
{
    public Dictionary<string, (long Free, bool Ready)> Disks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsReady(string path)
    {
        var root = Path.GetPathRoot(path) ?? path;
        return Disks.TryGetValue(root, out var info) && info.Ready;
    }

    public long GetAvailableFreeSpace(string path)
    {
        var root = Path.GetPathRoot(path) ?? path;
        if (Disks.TryGetValue(root, out var info))
        {
            return info.Free;
        }
        return 50L * 1024L * 1024L * 1024L;
    }

    public bool TestWriteAccess(string path) => true;
}

public class StorageManagerTests
{
    private const long OneGb = 1024L * 1024L * 1024L;

    [Fact]
    public void SelectLocation_SelectsPrimary_WhenSpaceIsHealthy()
    {
        var probe = new FakeDiskSpaceProbe();
        probe.Disks[@"C:\"] = (50L * OneGb, true);
        probe.Disks[@"D:\"] = (100L * OneGb, true);

        var settings = new StorageSettings
        {
            Locations =
            [
                new StorageLocationConfig { Path = @"C:\ScreenVault", MinFreeGb = 5, Enabled = true },
                new StorageLocationConfig { Path = @"D:\ScreenVaultBackup", MinFreeGb = 10, Enabled = true }
            ]
        };

        using var manager = new StorageManager(settings, probe);
        var selected = manager.SelectLocationForNewSegment(50L * 1024L * 1024L);

        Assert.Equal(@"C:\ScreenVault", selected);
        Assert.False(manager.GetStatus().IsInEmergencyMode);
    }

    [Fact]
    public void SelectLocation_FailsOverToBackup_WhenPrimaryBelowThreshold()
    {
        var probe = new FakeDiskSpaceProbe();
        // Initially Primary C:\ is healthy (50 GB free)
        probe.Disks[@"C:\"] = (50L * OneGb, true);
        probe.Disks[@"D:\"] = (100L * OneGb, true);

        var settings = new StorageSettings
        {
            Locations =
            [
                new StorageLocationConfig { Path = @"C:\ScreenVault", MinFreeGb = 5, Enabled = true },
                new StorageLocationConfig { Path = @"D:\ScreenVaultBackup", MinFreeGb = 5, Enabled = true }
            ]
        };

        var switchEventFired = false;
        using var manager = new StorageManager(settings, probe);
        var initial = manager.SelectLocationForNewSegment(50L * 1024L * 1024L);
        Assert.Equal(@"C:\ScreenVault", initial);

        manager.SwitchRequested += (_, e) =>
        {
            switchEventFired = true;
            Assert.Equal(@"D:\ScreenVaultBackup", e.NewLocation);
        };

        // Primary drops below 5 GB threshold (to 4 GB)
        probe.Disks[@"C:\"] = (4L * OneGb, true);
        var failover = manager.SelectLocationForNewSegment(50L * 1024L * 1024L);

        Assert.Equal(@"D:\ScreenVaultBackup", failover);
        Assert.True(switchEventFired);
    }

    [Fact]
    public void SelectLocation_Hysteresis_DoesNotFailback_UntilPrimaryHas10GbExtra()
    {
        var probe = new FakeDiskSpaceProbe();
        // Initially C:\ has 4 GB (fails over to D:\)
        probe.Disks[@"C:\"] = (4L * OneGb, true);
        probe.Disks[@"D:\"] = (100L * OneGb, true);

        var settings = new StorageSettings
        {
            Locations =
            [
                new StorageLocationConfig { Path = @"C:\ScreenVault", MinFreeGb = 5, Enabled = true },
                new StorageLocationConfig { Path = @"D:\ScreenVaultBackup", MinFreeGb = 5, Enabled = true }
            ],
            FailbackToPrimary = true
        };

        using var manager = new StorageManager(settings, probe);
        var firstSelected = manager.SelectLocationForNewSegment(50L * 1024L * 1024L);
        Assert.Equal(@"D:\ScreenVaultBackup", firstSelected);

        // User frees up 8 GB on C:\ (total 12 GB, minFree is 5 GB -> 12 < 5 + 10 GB hysteresis)
        probe.Disks[@"C:\"] = (12L * OneGb, true);
        var secondSelected = manager.SelectLocationForNewSegment(50L * 1024L * 1024L);
        Assert.Equal(@"D:\ScreenVaultBackup", secondSelected); // Stays on D:\ due to hysteresis!

        // User frees up 16 GB on C:\ (total 20 GB, minFree is 5 GB -> 20 > 5 + 10 GB hysteresis)
        probe.Disks[@"C:\"] = (20L * OneGb, true);
        var thirdSelected = manager.SelectLocationForNewSegment(50L * 1024L * 1024L);
        Assert.Equal(@"C:\ScreenVault", thirdSelected); // Now fails back!
    }

    [Fact]
    public void ReportWriteFailure_MarksLocationFailed_AndSelectsNext()
    {
        var probe = new FakeDiskSpaceProbe();
        probe.Disks[@"C:\"] = (50L * OneGb, true);
        probe.Disks[@"D:\"] = (100L * OneGb, true);

        var settings = new StorageSettings
        {
            Locations =
            [
                new StorageLocationConfig { Path = @"C:\ScreenVault", MinFreeGb = 5, Enabled = true },
                new StorageLocationConfig { Path = @"D:\ScreenVaultBackup", MinFreeGb = 5, Enabled = true }
            ]
        };

        using var manager = new StorageManager(settings, probe);
        manager.ReportWriteFailure(@"C:\ScreenVault", new IOException("Disk unplugged"));

        var selected = manager.SelectLocationForNewSegment(50L * 1024L * 1024L);
        Assert.Equal(@"D:\ScreenVaultBackup", selected);
    }

    [Fact]
    public void SelectLocation_ThrowsHardFloorException_WhenBelowHardFloor()
    {
        var probe = new FakeDiskSpaceProbe();
        // C: is system drive, has only 500 MB free (< 5 GB hard floor)
        probe.Disks[@"C:\"] = (500L * 1024L * 1024L, true);

        var settings = new StorageSettings
        {
            Locations =
            [
                new StorageLocationConfig { Path = @"C:\ScreenVault", MinFreeGb = 5, Enabled = true }
            ]
        };

        using var manager = new StorageManager(settings, probe);
        Assert.Throws<IOException>(() => manager.SelectLocationForNewSegment(50L * 1024L * 1024L));
    }

    [Fact]
    public void StorageManager_ExpandsEnvironmentVariables_AndSelectsRootedPath()
    {
        var probe = new FakeDiskSpaceProbe();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.GetPathRoot(userProfile) ?? @"C:\";
        probe.Disks[root] = (50L * OneGb, true);

        var settings = new StorageSettings
        {
            Locations =
            [
                new StorageLocationConfig { Path = @"%USERPROFILE%\ScreenVaultTest", MinFreeGb = 5, Enabled = true }
            ]
        };

        using var manager = new StorageManager(settings, probe);
        var selected = manager.SelectLocationForNewSegment(50L * 1024L * 1024L);

        var expected = Path.Combine(userProfile, "ScreenVaultTest");
        Assert.Equal(expected, selected);
        Assert.True(Path.IsPathRooted(selected));
    }

    [Fact]
    public void StorageManager_RejectsUnrootedRelativePath()
    {
        var probe = new FakeDiskSpaceProbe();
        var settings = new StorageSettings
        {
            Locations =
            [
                new StorageLocationConfig { Path = @"relative\folder\not\rooted", MinFreeGb = 5, Enabled = true }
            ]
        };

        using var manager = new StorageManager(settings, probe);
        var status = manager.GetStatus();

        Assert.Single(status.Locations);
        Assert.Equal(StorageLocationState.Failed, status.Locations[0].State);
        Assert.False(status.Locations[0].Enabled);
    }
}
