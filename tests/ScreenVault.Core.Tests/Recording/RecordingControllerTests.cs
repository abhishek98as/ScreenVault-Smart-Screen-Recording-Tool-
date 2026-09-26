using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.Output;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.Recording;

public sealed class RecordingControllerTests
{
    private sealed class FakeFfmpegHost : IFfmpegHost
    {
        public bool IsRunning { get; set; }
        public Stream AudioInput { get; } = new MemoryStream();
        public long StdoutBytesTotal { get; set; } = 100;
        public DateTime LastStdoutActivityUtc { get; set; } = DateTime.UtcNow;
        public FfmpegProgress LastProgress { get; set; } = new() { Speed = 1.0, Fps = 15 };
        public long WorkingSet64 { get; set; } = 100 * 1024 * 1024;
#pragma warning disable CS0067
        public event EventHandler<FfmpegExitedEventArgs>? Exited;
#pragma warning restore CS0067

        public Task StartAsync(FfmpegLaunchSpec spec, ISegmentSink sink, CancellationToken ct)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(TimeSpan gracefulTimeout)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Kill() => IsRunning = false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioEngine : IAudioEngine
    {
        public bool EnsureCaptureRunningCalled { get; private set; }
        public bool AttachOutputCalled { get; private set; }
        public bool DetachOutputCalled { get; private set; }
        public bool IsMicMuted { get; set; }
        public string LastSwitchSummary => "None";
        public List<AudioDeviceStatus> Devices { get; } = [];

        public event EventHandler<AudioDeviceSwitchedEventArgs>? DeviceSwitched;

        public void RaiseDeviceSwitched(string detail) =>
            DeviceSwitched?.Invoke(this, new AudioDeviceSwitchedEventArgs { Detail = detail });

        public void Start(AudioSettings settings) { }
        public void EnsureCaptureRunning() => EnsureCaptureRunningCalled = true;
        public void AttachOutput(Stream ffmpegStdin) => AttachOutputCalled = true;
        public void DetachOutput() => DetachOutputCalled = true;
        public void SetMonitoring(bool enabled) { }
        public void SetMicMute(bool muted) => IsMicMuted = muted;
        public void ApplySettings(AudioSettings settings) { }

        public AudioStatus GetStatus() => new(
            Devices,
            [],
            0,
            AttachOutputCalled,
            LastSwitchSummary,
            IsMicMuted,
            Devices.Any(d => !d.IsLoopback) ? "✔ Mic" : "✖ None",
            Devices.Any(d => d.IsLoopback) ? "✔ System" : "✖ None");

        public void Dispose() { }
    }

    private sealed class FakeSegmentSink : ISegmentSink
    {
        public SegmentInfo? Current { get; set; }
        public long BytesWritten { get; set; }
        public bool StreamEnded { get; private set; }

#pragma warning disable CS0067
        public event EventHandler<SegmentInfo>? SegmentClosed;
#pragma warning restore CS0067

        public void BeginStream(DateTime? sessionStartLocal = null, int initialPartIndex = 1)
        {
            StreamEnded = false;
        }

        public void Write(ReadOnlySpan<byte> tsBytes)
        {
            BytesWritten += tsBytes.Length;
        }

        public void RequestRotation(RotationReason reason) { }

        public void EndStream()
        {
            StreamEnded = true;
        }
    }

    private sealed class ManualClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        public long Timestamp { get; set; } = 1000;
        public double SecondsBetween(long startTimestamp, long endTimestamp) => (endTimestamp - startTimestamp) / 1000.0;
        public void Advance(TimeSpan span) => UtcNow = UtcNow.Add(span);
    }

    [Fact]
    public async Task StartAsync_CallsEnsureCaptureRunning_BeforeAttachOutput()
    {
        var settingsService = new SettingsService(customSettingsPath: @"C:\settings.json", fileSystem: new MockFileSystem());
        var ffmpeg = new FakeFfmpegHost();
        var audio = new FakeAudioEngine();
        var sink = new FakeSegmentSink();
        var clock = new ManualClock();

        await using var controller = new RecordingController(
            settingsService, ffmpeg, audio, sink, clock);

        await controller.StartAsync();

        audio.EnsureCaptureRunningCalled.ShouldBeTrue();
        audio.AttachOutputCalled.ShouldBeTrue();
        ffmpeg.IsRunning.ShouldBeTrue();
        controller.Desired.ShouldBe(DesiredState.Recording);
    }

    [Fact]
    public async Task PauseAsync_StopsFfmpeg_AndDetachesAudio()
    {
        var settingsService = new SettingsService(customSettingsPath: @"C:\settings.json", fileSystem: new MockFileSystem());
        var ffmpeg = new FakeFfmpegHost();
        var audio = new FakeAudioEngine();
        var sink = new FakeSegmentSink();
        var clock = new ManualClock();

        await using var controller = new RecordingController(
            settingsService, ffmpeg, audio, sink, clock);

        await controller.StartAsync();
        await controller.PauseAsync();

        controller.Desired.ShouldBe(DesiredState.Paused);
        controller.State.ShouldBe(RecorderState.Paused);
        audio.DetachOutputCalled.ShouldBeTrue();
        sink.StreamEnded.ShouldBeTrue();
        ffmpeg.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task Health_ReportsBytesWritten_Accurately()
    {
        var settingsService = new SettingsService(customSettingsPath: @"C:\settings.json", fileSystem: new MockFileSystem());
        var ffmpeg = new FakeFfmpegHost();
        var audio = new FakeAudioEngine();
        var sink = new FakeSegmentSink { BytesWritten = 42 * 1024 * 1024 };
        var clock = new ManualClock();

        await using var controller = new RecordingController(
            settingsService, ffmpeg, audio, sink, clock);

        var health = controller.Health;
        health.CurrentFileBytes.ShouldBe(42 * 1024 * 1024);
    }

    [Fact]
    public async Task AudioDeviceSwitched_TriggersNotification_WhenFired()
    {
        var settingsService = new SettingsService(customSettingsPath: @"C:\settings.json", fileSystem: new MockFileSystem());
        var ffmpeg = new FakeFfmpegHost();
        var audio = new FakeAudioEngine();
        var sink = new FakeSegmentSink();
        var clock = new ManualClock();

        await using var controller = new RecordingController(
            settingsService, ffmpeg, audio, sink, clock);

        var switchedDetail = string.Empty;
        controller.AudioDeviceSwitchedNotification += (_, detail) => switchedDetail = detail;

        audio.RaiseDeviceSwitched("12:00 — Headset Mic");

        switchedDetail.ShouldBe("12:00 — Headset Mic");
    }

    [Fact]
    public async Task PartIndex_StartsAtOne_AndIncrementsOnResume()
    {
        var settingsService = new SettingsService(customSettingsPath: @"C:\settings.json", fileSystem: new MockFileSystem());
        var ffmpeg = new FakeFfmpegHost();
        var audio = new FakeAudioEngine();
        var sink = new FakeSegmentSink();
        var clock = new ManualClock();

        await using var controller = new RecordingController(
            settingsService, ffmpeg, audio, sink, clock);

        await controller.StartAsync();
        controller.Health.PartIndex.ShouldBe(1);

        await controller.PauseAsync();
        controller.Health.PartIndex.ShouldBe(1);

        await controller.ResumeAsync();
        controller.Health.PartIndex.ShouldBe(2);
    }

    [Fact]
    public async Task StopAsync_WithZeroBytesWritten_DeletesManifestLeavingNoOrphan()
    {
        var mockFs = new MockFileSystem();
        var settingsService = new SettingsService(customSettingsPath: @"C:\settings.json", fileSystem: mockFs);
        var ffmpeg = new FakeFfmpegHost { StdoutBytesTotal = 0 };
        var audio = new FakeAudioEngine();
        var sink = new FakeSegmentSink { BytesWritten = 0 };
        var clock = new ManualClock();
        var sessionStore = new SessionStore(mockFs, @"C:\AppData\ScreenVault\sessions");

        await using var controller = new RecordingController(
            settingsService, ffmpeg, audio, sink, clock, sessionStore: sessionStore);

        await controller.StartAsync();
        // Zero bytes written, then stopped
        await controller.StopAsync();

        controller.State.ShouldBe(RecorderState.Idle);
        sessionStore.LoadAllCanonical().ShouldBeEmpty();
    }
}
