using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenVault.App.Platform;
using ScreenVault.App.UI;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Audio;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using Shouldly;
using Xunit;

namespace ScreenVault.IntegrationTests;

[Collection("UI")]
public sealed class UiSmokeTests
{
    private sealed class MockSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = AppSettings.CreateDefault();
        public string SettingsFilePath => "mock_settings.json";
        public bool WasMigrated => false;
        public event EventHandler<AppSettings>? SettingsChanged;
        public void Save(AppSettings settings)
        {
            Current = settings.Clone();
            SettingsChanged?.Invoke(this, Current);
        }
        public void Reload() { }
    }

    private sealed class MockAudioEngine : IAudioEngine
    {
        public bool IsMicMuted => false;
        public string LastSwitchSummary => string.Empty;
        public event EventHandler<AudioDeviceSwitchedEventArgs>? DeviceSwitched { add { } remove { } }
        public void ApplySettings(AudioSettings settings) { }
        public void AttachOutput(Stream ffmpegStdin) { }
        public void DetachOutput() { }
        public void Dispose() { }
        public void EnsureCaptureRunning() { }
        public AudioStatus GetStatus() => new(
            ActiveDevices: [],
            DegradedWarnings: [],
            DroppedChunks: 0,
            IsPumping: true);
        public void SetMicMute(bool muted) { }
        public void SetMonitoring(bool enabled) { }
        public void Start(AudioSettings settings) { }
    }

    private sealed class MockRecordingController : IRecordingController
    {
        public RecorderState State => RecorderState.Idle;
        public DesiredState Desired => DesiredState.Stopped;
        public HealthSnapshot Health => new(
            State: RecorderState.Idle,
            Desired: DesiredState.Stopped,
            IsDegraded: false,
            Elapsed: TimeSpan.Zero,
            CurrentFilePath: null,
            CurrentFileBytes: 0,
            AudioDevices: [],
            DegradedWarnings: [],
            EncoderProfile: "Default",
            ActualFps: 30.0,
            Speed: 1.0);
        public bool IsMicMuted => false;
        public event EventHandler<HealthSnapshot>? HealthChanged { add { } remove { } }
        public event EventHandler? PauseReminderTriggered { add { } remove { } }
        public event EventHandler<int>? FpsDegradedNotification { add { } remove { } }
        public event EventHandler? NoAudioSourcesNotification { add { } remove { } }
        public event EventHandler? DiskWriteStallNotification { add { } remove { } }
        public event EventHandler<string>? AudioDeviceSwitchedNotification { add { } remove { } }
        public event EventHandler<SessionManifest>? SessionCompleted { add { } remove { } }
        public void AddMarker(string? note, string kind = "User") { }
        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SetMicMute(bool muted) { }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ToggleMicMute() { }
    }

    private static void RunInSta(Action action, int timeoutMs = 60000)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => threadEx = e.Exception;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        bool finished = thread.Join(timeoutMs);
        if (!finished)
        {
            thread.Interrupt();
            throw new TimeoutException($"UI smoke test timed out after {timeoutMs} ms.");
        }

        if (threadEx != null)
        {
            throw new InvalidOperationException("UI test thread caught exception: " + threadEx.Message, threadEx);
        }
    }

    private static readonly AppThemeMode[] ThemeModes = [AppThemeMode.Light, AppThemeMode.Dark];
    private static readonly string[] PageNames =
    [
        "General", "Pause & resume", "Meetings", "Video",
        "Audio", "Recordings", "Notifications", "Shortcuts",
        "Advanced", "About"
    ];

    [Fact]
    public void SettingsForm_AllPagesRenderWithoutCrashing_InLightAndDarkThemes()
    {
        RunInSta(() =>
        {
            var settingsService = new MockSettingsService();
            var outDir = Path.Combine(Path.GetTempPath(), "sv_ui_smoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);

            foreach (var themeMode in ThemeModes)
            {
                Theme.SetMode(themeMode);

                using var form = new SettingsForm(settingsService);
                form.Show();
                Application.DoEvents();

                for (var i = 0; i < PageNames.Length; i++)
                {
                    form.SelectPage(PageNames[i]);
                    Application.DoEvents();

                    using var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));

                    var imgPath = Path.Combine(outDir, $"{themeMode}_{i}_{PageNames[i].Replace(" ", "_")}.png");
                    bmp.Save(imgPath, ImageFormat.Png);
                    File.Exists(imgPath).ShouldBeTrue();
                }

                form.Close();
            }

            try { Directory.Delete(outDir, true); } catch { }
        });
    }

    [Fact]
    public void FirstRunWizardForm_OpensAndRenders()
    {
        RunInSta(() =>
        {
            var settingsService = new MockSettingsService();
            using var wizard = new FirstRunWizardForm(settingsService, new MockAudioEngine());
            wizard.Show();
            Application.DoEvents();

            using var bmp = new Bitmap(wizard.Width, wizard.Height);
            wizard.DrawToBitmap(bmp, new Rectangle(0, 0, wizard.Width, wizard.Height));
            bmp.Width.ShouldBeGreaterThan(0);

            wizard.Close();
        });
    }

    [Fact]
    public void LibraryForm_OpensAndRenders()
    {
        RunInSta(() =>
        {
            var settingsService = new MockSettingsService();
            var sessionStore = new SessionStore();
            var launcher = new PlayerLauncher(settingsService);
            var exporter = new ClipExporter(sessionStore);
            var controller = new MockRecordingController();

            using var lib = new LibraryForm(sessionStore, controller, settingsService, launcher, exporter);
            lib.Show();
            Application.DoEvents();

            using var bmp = new Bitmap(lib.Width, lib.Height);
            lib.DrawToBitmap(bmp, new Rectangle(0, 0, lib.Width, lib.Height));
            bmp.Width.ShouldBeGreaterThan(0);

            lib.Close();
        });
    }
}
