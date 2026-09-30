using ScreenVault.Core.Recording;
using ScreenVault.Core.Settings;
using ScreenVault.Core.SystemIntegration;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.SystemIntegration;

public sealed class AutoPauseServiceTests
{
    private sealed class FakeRecorder : IPausableRecorder
    {
        public bool IsRecording { get; set; } = true;
        public bool IsManuallyPaused { get; set; }
        public List<PauseReason> Pauses { get; } = [];
        public int ResumeCalls { get; private set; }
        public List<(string? note, string kind)> Markers { get; } = [];

        public Task PauseAsync(PauseReason reason, CancellationToken ct = default)
        {
            Pauses.Add(reason);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken ct = default)
        {
            ResumeCalls++;
            return Task.CompletedTask;
        }

        public void AddMarker(string? note, string kind = "User")
        {
            Markers.Add((note, kind));
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = AppSettings.CreateDefault();
        public string SettingsFilePath => "test_settings.json";
        public bool WasMigrated => false;
        public event EventHandler<AppSettings>? SettingsChanged { add { } remove { } }
        public void Save(AppSettings settings) => Current = settings;
        public void Reload() { }
    }

    [Fact]
    public void SessionLocked_WhenConfigured_PausesWithLockedReason()
    {
        var recorder = new FakeRecorder();
        var settings = new FakeSettingsService();
        settings.Current.AutoPause.PauseWhenLocked = true;
        using var detector = new MeetingDetector(settings);
        using var service = new AutoPauseService(recorder, settings, detector);

        service.OnSessionLocked();

        recorder.Pauses.Count.ShouldBe(1);
        recorder.Pauses[0].ShouldBe(PauseReason.Locked);
    }

    [Fact]
    public void SessionLocked_WhenDisabled_DoesNotPause()
    {
        var recorder = new FakeRecorder();
        var settings = new FakeSettingsService();
        settings.Current.AutoPause.PauseWhenLocked = false;
        using var detector = new MeetingDetector(settings);
        using var service = new AutoPauseService(recorder, settings, detector);

        service.OnSessionLocked();

        recorder.Pauses.ShouldBeEmpty();
    }

    [Fact]
    public void SessionUnlocked_ResumesRecording()
    {
        var recorder = new FakeRecorder();
        var settings = new FakeSettingsService();
        settings.Current.AutoPause.PauseWhenLocked = true;
        settings.Current.AutoPause.ResumeWhenBack = true;
        using var detector = new MeetingDetector(settings);
        using var service = new AutoPauseService(recorder, settings, detector);

        service.OnSessionLocked();
        recorder.Pauses.Count.ShouldBe(1);

        service.OnSessionUnlocked();
        recorder.ResumeCalls.ShouldBe(1);
    }

    [Fact]
    public void SuspendAndResume_HandlesSleepAccurately()
    {
        var recorder = new FakeRecorder();
        var settings = new FakeSettingsService();
        settings.Current.AutoPause.PauseOnSleep = true;
        settings.Current.AutoPause.ResumeWhenBack = true;
        using var detector = new MeetingDetector(settings);
        using var service = new AutoPauseService(recorder, settings, detector);

        service.OnSuspend();
        recorder.Pauses.Count.ShouldBe(1);
        recorder.Pauses[0].ShouldBe(PauseReason.Asleep);

        service.OnResume();
        recorder.ResumeCalls.ShouldBe(1);
    }

    [Fact]
    public void ManualPause_TakesPrecedenceOverAutoPause()
    {
        var recorder = new FakeRecorder { IsManuallyPaused = true };
        var settings = new FakeSettingsService();
        settings.Current.AutoPause.PauseWhenLocked = true;
        using var detector = new MeetingDetector(settings);
        using var service = new AutoPauseService(recorder, settings, detector);

        service.OnSessionLocked();

        recorder.Pauses.ShouldBeEmpty();
    }

    [Fact]
    public void DisplayOff_AddsMarkerAndPauses()
    {
        var recorder = new FakeRecorder();
        var settings = new FakeSettingsService();
        settings.Current.AutoPause.PauseOnSleep = true;
        using var detector = new MeetingDetector(settings);
        using var service = new AutoPauseService(recorder, settings, detector);

        service.OnDisplayOff();

        recorder.Pauses.Count.ShouldBe(1);
        recorder.Pauses[0].ShouldBe(PauseReason.Away);
        recorder.Markers.ShouldContain(m => m.kind == "AutoPause" && m.note!.Contains("display off"));
    }
}
