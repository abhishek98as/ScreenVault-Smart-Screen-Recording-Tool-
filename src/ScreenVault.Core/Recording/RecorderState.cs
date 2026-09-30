using ScreenVault.Core.Audio;

namespace ScreenVault.Core.Recording;

public enum RecorderState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Saving,
    Recovering,
    Faulted,
    Suspended,
    Stopping
}

public enum DesiredState
{
    Stopped,
    Recording,
    Paused
}

/// <summary>Why a recording was paused. User = manually paused by the user. All others are automatic.</summary>
public enum PauseReason
{
    User,
    Away,
    Locked,
    Asleep
}

public sealed record HealthSnapshot(
    RecorderState State,
    DesiredState Desired,
    bool IsDegraded,
    TimeSpan Elapsed,
    string? CurrentFilePath,
    long CurrentFileBytes,
    IReadOnlyList<AudioDeviceStatus> AudioDevices,
    IReadOnlyList<string> DegradedWarnings,
    string EncoderProfile,
    double ActualFps,
    double Speed,
    TimeSpan PartElapsed = default,
    int PartIndex = 1,
    long FfmpegWorkingSetBytes = 0,
    PauseReason? AutoPauseReason = null);
