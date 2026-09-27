using System.IO.Abstractions;
using ScreenVault.Core.Infrastructure;
using Serilog;

namespace ScreenVault.Core.Output;

public delegate string LocationSelector(long estimatedBytes);
public delegate void WriteFailureHandler(string failedLocation, Exception ex);

public sealed class SegmentWriter : ISegmentSink, IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly TsPacketAligner _aligner = new();
    private readonly TsPsiCache _psiCache = new();
    private readonly LocationSelector _locationSelector;
    private readonly WriteFailureHandler? _onWriteFailure;
    private readonly Action<SegmentInfo>? _onSegmentClosed;
    private readonly object _lock = new();

    private readonly MemoryStream _unflushedTail = new();
    private Stream? _currentStream;
    private string _currentFilePath = string.Empty;
    private string _currentLocation = string.Empty;
    private int _partIndex;
    private DateTime _sessionStartLocal;
    private DateTime _segmentStartLocal;
    private DateTime _segmentStartUtc;
    private long _segmentStartTimestamp;
    private long _segmentBytesWritten;
    private long _lastFlushTimestamp;
    private int _flushCount;
    private long _lastFlushLogTimestamp;
    private RotationReason _currentOpenReason = RotationReason.SessionStart;

    private bool _rotationRequested;
    private RotationReason _pendingRotationReason;
    private long _rotationRequestedTimestamp;
    private bool _streamActive;

    private TimeSpan? _splitAfter;
    private long? _splitAtBytes;

    public SegmentInfo? Current { get; private set; }
    public long BytesWritten => Interlocked.Read(ref _segmentBytesWritten);
    public event EventHandler<SegmentInfo>? SegmentClosed;

    public SegmentWriter(
        LocationSelector locationSelector,
        WriteFailureHandler? onWriteFailure = null,
        Action<SegmentInfo>? onSegmentClosed = null,
        IFileSystem? fileSystem = null,
        IClock? clock = null)
    {
        _locationSelector = locationSelector ?? throw new ArgumentNullException(nameof(locationSelector));
        _onWriteFailure = onWriteFailure;
        _onSegmentClosed = onSegmentClosed;
        _fileSystem = fileSystem ?? new FileSystem();
        _clock = clock ?? SystemClock.Instance;
    }

    public void ConfigureSplitting(TimeSpan? maxDuration, long? maxBytes)
    {
        lock (_lock)
        {
            _splitAfter = maxDuration is { } d && d > TimeSpan.Zero ? d : null;
            _splitAtBytes = maxBytes is > 0 ? maxBytes : null;
        }
    }

    public void BeginStream(DateTime? sessionStartLocal = null, int initialPartIndex = 1)
    {
        lock (_lock)
        {
            if (_currentStream != null)
            {
                // A previous stream was never ended (e.g. a failed start): close it properly first.
                Log.Warning("SegmentWriter: previous stream was still open; closing it before starting a new one.");
                CloseCurrentSegment(RotationReason.Manual);
            }

            _streamActive = true;
            _partIndex = initialPartIndex;
            _sessionStartLocal = sessionStartLocal ?? DateTime.Now;
            _rotationRequested = false;
            _aligner.Reset();
            _psiCache.Reset();
            _unflushedTail.SetLength(0);

            // Counters describe the new stream only; stale values from the previous recording would
            // make the recorder believe data had already reached the disk.
            Interlocked.Exchange(ref _segmentBytesWritten, 0);
            Current = null;

            _currentOpenReason = initialPartIndex > 1 ? RotationReason.Manual : RotationReason.SessionStart;
            Log.Information("SegmentWriter: stream begun for session {SessionId}, starting at part {Part}",
                SegmentNaming.FormatSessionId(_sessionStartLocal), _partIndex);
        }
    }

    public void Write(ReadOnlySpan<byte> tsBytes)
    {
        lock (_lock)
        {
            if (!_streamActive) return;

            _aligner.Feed(tsBytes, ProcessPacket);
        }
    }

    private void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        var header = new TsHeader(packet);
        if (!header.HasSyncByte) return;

        _psiCache.ProcessPacket(packet, in header);

        // Check for rotation if requested
        if (_rotationRequested)
        {
            var isVideoPid = _psiCache.VideoPid == -1
                ? (header.Pid != 0 && header.Pid != _psiCache.PmtPid)
                : header.Pid == _psiCache.VideoPid;

            var isKeyframeStart = isVideoPid &&
                                  header.PayloadUnitStartIndicator &&
                                  header.RandomAccessIndicator;

            var timeSinceRequestSec = _clock.SecondsBetween(_rotationRequestedTimestamp, _clock.Timestamp);
            var forceTimeout = timeSinceRequestSec >= 5.0;

            if (isKeyframeStart || forceTimeout)
            {
                if (forceTimeout && !isKeyframeStart)
                {
                    Log.Warning("SegmentWriter: Keyframe not detected within 5 seconds of rotation request. Forcing split at packet boundary.");
                }
                RotateSegment(_pendingRotationReason);
            }
        }

        // Ensure active segment file is open
        if (_currentStream == null)
        {
            OpenNewSegment();
        }

        WritePacketToCurrent(packet);

        // Check 1-second durable flush
        var elapsedSinceFlush = _clock.SecondsBetween(_lastFlushTimestamp, _clock.Timestamp);
        if (elapsedSinceFlush >= 1.0)
        {
            FlushDurable();
        }

        CheckSplitLimits();
    }

    private void CheckSplitLimits()
    {
        if (_rotationRequested || _currentStream == null)
        {
            return;
        }

        if (_splitAfter is { } maxDuration &&
            _clock.SecondsBetween(_segmentStartTimestamp, _clock.Timestamp) >= maxDuration.TotalSeconds)
        {
            RequestRotationLocked(RotationReason.TimeSplit);
        }
        else if (_splitAtBytes is { } maxBytes && _segmentBytesWritten >= maxBytes)
        {
            RequestRotationLocked(RotationReason.SizeSplit);
        }
    }

    private void OpenNewSegment()
    {
        _segmentStartLocal = DateTime.Now;
        _segmentStartUtc = _clock.UtcNow;
        _segmentStartTimestamp = _clock.Timestamp;
        Interlocked.Exchange(ref _segmentBytesWritten, 0);
        var rawLocation = _locationSelector(150 * 1024 * 1024); // default 150 MB estimate
        _currentLocation = Environment.ExpandEnvironmentVariables(rawLocation);
        if (!_fileSystem.Path.IsPathRooted(_currentLocation))
        {
            throw new InvalidOperationException($"Storage location '{rawLocation}' (expanded to '{_currentLocation}') is not rooted.");
        }

        _currentFilePath = SegmentNaming.GenerateSegmentPath(
            _fileSystem,
            _currentLocation,
            _sessionStartLocal,
            _segmentStartLocal,
            _partIndex);

        Log.Information("Opening new segment part {Part}: {Path}", _partIndex, _currentFilePath);

        var dir = _fileSystem.Path.GetDirectoryName(_currentFilePath);
        if (!string.IsNullOrEmpty(dir) && !_fileSystem.Directory.Exists(dir))
        {
            _fileSystem.Directory.CreateDirectory(dir);
        }

        _currentStream = _fileSystem.FileStream.New(
            _currentFilePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1 << 20);

        _lastFlushTimestamp = _clock.Timestamp;
        _unflushedTail.SetLength(0);

        // Write cached PAT and PMT packets if available
        if (_psiCache.CachedPatPacket != null)
        {
            WriteRawBytes(_psiCache.CachedPatPacket);
        }
        if (_psiCache.CachedPmtPacket != null)
        {
            WriteRawBytes(_psiCache.CachedPmtPacket);
        }

        UpdateCurrentInfo();
    }

    private void WritePacketToCurrent(ReadOnlySpan<byte> packet)
    {
        try
        {
            WriteRawBytes(packet);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Write error on {Path}. Attempting zero-gap failover.", _currentFilePath);
            HandleWriteFailure(ex, packet);
        }
    }

    private void WriteRawBytes(ReadOnlySpan<byte> bytes)
    {
        if (_currentStream == null) return;

        _currentStream.Write(bytes);
        Interlocked.Add(ref _segmentBytesWritten, bytes.Length);
        _unflushedTail.Write(bytes);
    }

    private void HandleWriteFailure(Exception ex, ReadOnlySpan<byte> failedPacket)
    {
        _onWriteFailure?.Invoke(_currentLocation, ex);

        // Everything written since the last durable flush may never have reached the failed drive.
        // Keep a copy now: opening the next segment resets the tail buffer.
        var tail = _unflushedTail.ToArray();

        // Close failing stream if possible
        try
        {
            _currentStream?.Dispose();
        }
        catch
        {
            // Ignore failure on close of broken drive
        }
        _currentStream = null;

        // Whatever did reach the failed drive is still part of the recording: keep it in the session.
        if (_segmentBytesWritten > 0)
        {
            RaiseSegmentClosed(RotationReason.StorageSwitch);
        }

        // Select new location and open new segment
        _partIndex++;
        _currentOpenReason = RotationReason.StorageSwitch;
        OpenNewSegment();

        // Write unflushed tail to new segment to prevent data loss
        if (tail.Length > 0)
        {
            Log.Information("Writing {Count} bytes of unflushed tail to new storage location", tail.Length);
            WriteRawBytes(tail);
        }

        // Write the packet that triggered the error
        WriteRawBytes(failedPacket);
        FlushDurable();
    }

    private void FlushDurable()
    {
        if (_currentStream == null) return;

        try
        {
            _currentStream.Flush();
            if (_currentStream is FileSystemStream fss)
            {
                fss.Flush(flushToDisk: true);
            }
            else if (_currentStream is FileStream fs)
            {
                fs.Flush(flushToDisk: true);
            }
            _flushCount++;
            _lastFlushTimestamp = _clock.Timestamp;
            _unflushedTail.SetLength(0);
            UpdateCurrentInfo();

            var secSinceLog = _clock.SecondsBetween(_lastFlushLogTimestamp, _clock.Timestamp);
            if (secSinceLog >= 60.0)
            {
                Log.Information("SegmentWriter: {FlushCount} durable flushes performed in last {Sec:F0}s (Current file size: {Bytes:N0} bytes)",
                    _flushCount, secSinceLog, _segmentBytesWritten);
                _flushCount = 0;
                _lastFlushLogTimestamp = _clock.Timestamp;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Durable flush failed on {Path}", _currentFilePath);
        }
    }

    public void RequestRotation(RotationReason reason)
    {
        lock (_lock)
        {
            if (!_streamActive) return;
            RequestRotationLocked(reason);
        }
    }

    private void RequestRotationLocked(RotationReason reason)
    {
        _rotationRequested = true;
        _pendingRotationReason = reason;
        _rotationRequestedTimestamp = _clock.Timestamp;
        Log.Information("Rotation requested. Reason: {Reason}", reason);
    }

    private void RotateSegment(RotationReason closeReason)
    {
        _rotationRequested = false;
        CloseCurrentSegment(closeReason);

        _partIndex++;
        _currentOpenReason = closeReason;
        OpenNewSegment();
    }

    public void EndStream()
    {
        lock (_lock)
        {
            if (!_streamActive) return;
            _streamActive = false;
            _rotationRequested = false;

            CloseCurrentSegment(RotationReason.Manual);
            Log.Information("SegmentWriter: stream ended cleanly.");
        }
    }

    /// <summary>Flushes and closes the open segment (if any) and reports it as finished.</summary>
    private void CloseCurrentSegment(RotationReason closeReason)
    {
        if (_currentStream == null)
        {
            return;
        }

        FlushDurable();
        try
        {
            _currentStream.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error closing segment stream {Path}", _currentFilePath);
        }

        _currentStream = null;
        RaiseSegmentClosed(closeReason);
    }

    private void RaiseSegmentClosed(RotationReason closeReason)
    {
        var closedSegment = new SegmentInfo(
            _partIndex,
            _currentLocation,
            _currentFilePath,
            _fileSystem.Path.ChangeExtension(_currentFilePath, ".mkv"),
            _segmentStartUtc,
            _clock.UtcNow,
            _segmentBytesWritten,
            _currentOpenReason,
            closeReason,
            "Pending");

        Current = closedSegment;
        Log.Information("Segment part {Part} closed. Bytes: {Bytes}, CloseReason: {Reason}",
            _partIndex, _segmentBytesWritten, closeReason);

        try
        {
            _onSegmentClosed?.Invoke(closedSegment);
            SegmentClosed?.Invoke(this, closedSegment);
        }
        catch (Exception ex)
        {
            // Bookkeeping must never stop the recording itself.
            Log.Error(ex, "Error while handling closed segment {Path}", _currentFilePath);
        }
    }

    private void UpdateCurrentInfo()
    {
        Current = new SegmentInfo(
            _partIndex,
            _currentLocation,
            _currentFilePath,
            _fileSystem.Path.ChangeExtension(_currentFilePath, ".mkv"),
            _segmentStartUtc,
            null,
            _segmentBytesWritten,
            _currentOpenReason,
            null,
            "Pending");
    }

    public void Dispose()
    {
        EndStream();
        _unflushedTail.Dispose();
    }
}
