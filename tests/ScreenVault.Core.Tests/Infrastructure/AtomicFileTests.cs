using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Infrastructure;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Infrastructure;

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteAllText_CreatesNewFileSuccessfully()
    {
        var fileSystem = new MockFileSystem();
        var atomicFile = new AtomicFile(fileSystem);
        var path = @"C:\data\test.txt";

        atomicFile.WriteAllText(path, "Hello, ScreenVault!");

        fileSystem.File.Exists(path).ShouldBeTrue();
        fileSystem.File.ReadAllText(path).ShouldBe("Hello, ScreenVault!");
    }

    [Fact]
    public void WriteAllText_WithBackup_ReplacesAndCreatesBackup()
    {
        var path = @"C:\data\settings.json";
        var backupPath = @"C:\data\settings.json.bak";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [path] = new MockFileData("{\"initial\":true}")
        });

        var atomicFile = new AtomicFile(fileSystem);
        atomicFile.WriteAllText(path, "{\"updated\":true}", backupPath);

        fileSystem.File.ReadAllText(path).ShouldBe("{\"updated\":true}");
        fileSystem.File.Exists(backupPath).ShouldBeTrue();
        fileSystem.File.ReadAllText(backupPath).ShouldBe("{\"initial\":true}");
    }

    [Fact]
    public void ReadAllText_FallsBackToBackup_WhenPrimaryMissing()
    {
        var path = @"C:\data\settings.json";
        var backupPath = @"C:\data\settings.json.bak";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [backupPath] = new MockFileData("{\"fromBackup\":true}")
        });

        var atomicFile = new AtomicFile(fileSystem);
        var text = atomicFile.ReadAllText(path, backupPath);

        text.ShouldBe("{\"fromBackup\":true}");
    }
}
