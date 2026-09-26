using ScreenVault.App.Platform;
using ScreenVault.Core.Settings;
using Shouldly;
using Xunit;

namespace ScreenVault.IntegrationTests;

public sealed class PlayerLauncherTests
{
    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = AppSettings.CreateDefault();
        public string SettingsFilePath => "dummy.json";
        public bool WasMigrated => false;
#pragma warning disable CS0067
        public event EventHandler<AppSettings>? SettingsChanged;
#pragma warning restore CS0067
        public void Save(AppSettings newSettings) => Current = newSettings;
        public void Reload() { }
    }

    [Fact]
    public void ResolvePlayer_ForTsFile_NeverReturnsSystemDefault()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current.Playback.Player = PlaybackPlayer.SystemDefault;

        var launcher = new PlayerLauncher(settingsService);
        var resolved = launcher.ResolvePlayer(@"C:\Recordings\test.ts");

        // Never open .ts with system default (avoid opening IDEs like VS Code)
        resolved.Type.ShouldNotBe(ResolvedPlayerType.SystemDefault);
    }

    [Fact]
    public void ResolvePlayer_ForMkvFile_CanReturnSystemDefault()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current.Playback.Player = PlaybackPlayer.SystemDefault;

        var launcher = new PlayerLauncher(settingsService);
        var resolved = launcher.ResolvePlayer(@"C:\Recordings\test.mkv");

        resolved.Type.ShouldBe(ResolvedPlayerType.SystemDefault);
    }

    [Fact]
    public void ResolvePlayer_Auto_ResolvesBundledFfplayOrVlc()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current.Playback.Player = PlaybackPlayer.Auto;

        var launcher = new PlayerLauncher(settingsService);
        var resolved = launcher.ResolvePlayer(@"C:\Recordings\test.ts");

        // Either VLC was detected on machine, or bundled ffplay was detected
        resolved.Type.ShouldBeOneOf(ResolvedPlayerType.Vlc, ResolvedPlayerType.Ffplay);
        resolved.ExecutablePath.ShouldNotBeNullOrWhiteSpace();
    }
}
