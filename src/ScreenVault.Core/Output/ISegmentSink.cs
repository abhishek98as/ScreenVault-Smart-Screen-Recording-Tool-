namespace ScreenVault.Core.Output;

public enum RotationReason
{
    SessionStart,
    TimeSplit,
    SizeSplit,
    Midnight,
    StorageSwitch,
    Failback,
    Manual
}

public sealed record SegmentInfo(
    int Index,
    string Location,
    string TsPath,
    string FinalPath,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    long Bytes,
    RotationReason OpenReason,
    RotationReason? CloseReason,
    string RemuxStatus);

public interface ISegmentSink
{
    void BeginStream(DateTime? sessionStartLocal = null, int initialPartIndex = 1);
    void Write(ReadOnlySpan<byte> tsBytes);
    void RequestRotation(RotationReason reason);
    void EndStream();
    SegmentInfo? Current { get; }
    long BytesWritten { get; }
    event EventHandler<SegmentInfo>? SegmentClosed;
}
