using ScreenVault.Core.Recording;

namespace ScreenVault.Core.SystemIntegration;

/// <summary>
/// Minimal interface that AutoPauseService needs from the recording controller,
/// allowing the service to be tested without the full RecordingController.
/// </summary>
public interface IPausableRecorder
{
    bool IsRecording { get; }
    bool IsManuallyPaused { get; }
    Task PauseAsync(PauseReason reason, CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    void AddMarker(string? note, string kind = "User");
}
