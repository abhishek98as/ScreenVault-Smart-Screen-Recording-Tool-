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
    public void ResolvePlayer_Auto_NeverUsesABuiltInPlayer()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current.Playback.Player = PlaybackPlayer.Auto;

        var launcher = new PlayerLauncher(settingsService);

        // The user's own video app, or Windows' "Open with" chooser — never a player of our own.
        launcher.ResolvePlayer(@"C:\Recordings\test.mkv").Type.ShouldBe(ResolvedPlayerType.SystemDefault);
        launcher.ResolvePlayer(@"C:\Recordings\test.ts").Type.ShouldBeOneOf(ResolvedPlayerType.SystemDefault, ResolvedPlayerType.AskUser);
    }

    [Fact]
    public void ResolvePlayer_RetiredBuiltInPlayerSetting_UsesTheWindowsDefault()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current.Playback.Player = PlaybackPlayer.Ffplay;

        var launcher = new PlayerLauncher(settingsService);

        launcher.ResolvePlayer(@"C:\Recordings\test.mp4").Type.ShouldBe(ResolvedPlayerType.SystemDefault);
    }

    [Fact]
    public void ResolvePlayer_CustomPlayer_IsHonored()
    {
        var player = Path.GetTempFileName();
        try
        {
            var settingsService = new FakeSettingsService();
            settingsService.Current.Playback.Player = PlaybackPlayer.Custom;
            settingsService.Current.Playback.CustomPlayerPath = player;

            var resolved = new PlayerLauncher(settingsService).ResolvePlayer(@"C:\Recordings\test.mkv");

            resolved.Type.ShouldBe(ResolvedPlayerType.Custom);
            resolved.ExecutablePath.ShouldBe(player);
        }
        finally
        {
            File.Delete(player);
        }
    }
}
