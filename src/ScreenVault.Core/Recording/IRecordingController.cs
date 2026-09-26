using ScreenVault.Core.Sessions;

namespace ScreenVault.Core.Recording;

public interface IRecordingController
{
    RecorderState State { get; }
    DesiredState Desired { get; }
    HealthSnapshot Health { get; }
    event EventHandler<HealthSnapshot>? HealthChanged;
    event EventHandler? PauseReminderTriggered;
    event EventHandler<int>? FpsDegradedNotification;
    event EventHandler? NoAudioSourcesNotification;
    event EventHandler? DiskWriteStallNotification;
    event EventHandler<string>? AudioDeviceSwitchedNotification;
    event EventHandler<SessionManifest>? SessionCompleted;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    void AddMarker(string? note, string kind = "User");
    bool IsMicMuted { get; }
    void ToggleMicMute();
    void SetMicMute(bool muted);
}
