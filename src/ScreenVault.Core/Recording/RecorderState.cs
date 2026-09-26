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
    long FfmpegWorkingSetBytes = 0);
