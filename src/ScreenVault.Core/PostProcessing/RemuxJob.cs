using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;

namespace ScreenVault.Core.PostProcessing;

public sealed class RemuxJob
{
    public required string TsPath { get; init; }
    public required string FinalPath { get; init; }
    public OutputContainerFormat OutputFormat { get; init; } = OutputContainerFormat.Mkv;
    public bool KeepTsAfterRemux { get; init; }
    public string? SessionTitle { get; init; }
    public IReadOnlyList<MarkerEntry> Markers { get; init; } = [];
    public DateTime SegmentStartUtc { get; init; }
    public DateTime SegmentEndUtc { get; init; }
    public string? SessionId { get; init; }
    public int SegmentIndex { get; init; }
    public int RetryCount { get; set; }
}

public sealed class RemuxCompletedEventArgs : EventArgs
{
    public RemuxJob Job { get; }
    public bool Success { get; }
    public string? ErrorMessage { get; }

    public RemuxCompletedEventArgs(RemuxJob job, bool success, string? errorMessage = null)
    {
        Job = job;
        Success = success;
        ErrorMessage = errorMessage;
    }
}
