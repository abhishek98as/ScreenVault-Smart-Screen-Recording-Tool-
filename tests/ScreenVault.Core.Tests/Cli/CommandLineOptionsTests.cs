using ScreenVault.Core.Cli;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Cli;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void Parse_ParsesAllFlagsAccurately()
    {
        var args = new[]
        {
            "--startrecording",
            "--minimize-to-tray",
            "--marker", "meeting sprint planning",
            "--portable"
        };

        var options = CommandLineOptions.Parse(args);

        options.StartRecording.ShouldBeTrue();
        options.MinimizeToTray.ShouldBeTrue();
        options.AddMarker.ShouldBeTrue();
        options.MarkerNote.ShouldBe("meeting sprint planning");
        options.Portable.ShouldBeTrue();
        options.StopRecording.ShouldBeFalse();
        options.Pause.ShouldBeFalse();
        options.Toggle.ShouldBeFalse();
    }

    [Fact]
    public void Parse_ToggleFlag_SetsToggleToTrue()
    {
        var options = CommandLineOptions.Parse(["--toggle"]);
        options.Toggle.ShouldBeTrue();
    }

    [Fact]
    public void Parse_StatusAndSettingsAndLogLevel_ParsesCorrectly()
    {
        var args = new[]
        {
            "--status",
            "--settings",
            "--pause",
            "--resume",
            "--exit",
            "--log-level", "Debug"
        };

        var options = CommandLineOptions.Parse(args);

        options.Status.ShouldBeTrue();
        options.ShowStatus.ShouldBeTrue();
        options.Settings.ShouldBeTrue();
        options.OpenSettings.ShouldBeTrue();
        options.Pause.ShouldBeTrue();
        options.Resume.ShouldBeTrue();
        options.Exit.ShouldBeTrue();
        options.LogLevel.ShouldBe("Debug");
    }

    [Fact]
    public void Parse_InstallerFlags_ParsesAutostartAfterUpgradeAndWait()
    {
        var args = new[]
        {
            "--autostart",
            "--after-upgrade",
            "--exit",
            "--wait"
        };

        var options = CommandLineOptions.Parse(args);

        options.Autostart.ShouldBeTrue();
        options.AfterUpgrade.ShouldBeTrue();
        options.Exit.ShouldBeTrue();
        options.Wait.ShouldBeTrue();
    }
}

