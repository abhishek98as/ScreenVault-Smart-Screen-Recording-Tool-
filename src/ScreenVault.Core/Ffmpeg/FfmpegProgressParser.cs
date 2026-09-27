using System.Globalization;

namespace ScreenVault.Core.Ffmpeg;

public sealed record FfmpegProgress(
    long Frame = 0,
    double Fps = 0,
    double Speed = 0,
    TimeSpan OutTime = default,
    long TotalBytes = 0);

public sealed class FfmpegProgressParser
{
    private long _currentFrame;
    private double _currentFps;
    private double _currentSpeed;
    private TimeSpan _currentOutTime;
    private long _currentTotalBytes;

    public FfmpegProgress ParseLine(string line, out bool isSnapshotComplete)
    {
        isSnapshotComplete = false;
        if (string.IsNullOrWhiteSpace(line))
        {
            return CurrentProgress();
        }

        var eqIndex = line.IndexOf('=');
        if (eqIndex <= 0)
        {
            return CurrentProgress();
        }

        var key = line[..eqIndex].Trim();
        var val = line[(eqIndex + 1)..].Trim();

        switch (key)
        {
            case "frame":
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
                {
                    _currentFrame = frame;
                }
                break;

            case "fps":
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
                {
                    _currentFps = fps;
                }
                break;

            case "speed":
                var speedStr = val.TrimEnd('x');
                if (double.TryParse(speedStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                {
                    _currentSpeed = speed;
                }
                break;

            case "out_time_us":
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                {
                    _currentOutTime = TimeSpan.FromMilliseconds(micros / 1000.0);
                }
                break;

            case "total_size":
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
                {
                    _currentTotalBytes = bytes;
                }
                break;

            case "progress":
                isSnapshotComplete = true;
                break;
        }

        return CurrentProgress();
    }

    public FfmpegProgress CurrentProgress() =>
        new(_currentFrame, _currentFps, _currentSpeed, _currentOutTime, _currentTotalBytes);

    /// <summary>Forgets the previous process' figures so watchdogs don't act on stale speed/fps.</summary>
    public void Reset()
    {
        _currentFrame = 0;
        _currentFps = 0;
        _currentSpeed = 0;
        _currentOutTime = TimeSpan.Zero;
        _currentTotalBytes = 0;
    }
}
