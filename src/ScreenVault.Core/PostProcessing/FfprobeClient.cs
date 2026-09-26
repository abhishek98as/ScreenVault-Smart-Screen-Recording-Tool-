using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenVault.Core.Ffmpeg;
using Serilog;

namespace ScreenVault.Core.PostProcessing;

public sealed class ProbeStreamInfo
{
    [JsonPropertyName("codec_type")]
    public string CodecType { get; set; } = string.Empty;

    [JsonPropertyName("codec_name")]
    public string CodecName { get; set; } = string.Empty;

    [JsonPropertyName("pix_fmt")]
    public string? PixFmt { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("sample_rate")]
    public string? SampleRate { get; set; }

    [JsonPropertyName("channels")]
    public int? Channels { get; set; }
}

public sealed class ProbeFormat
{
    [JsonPropertyName("duration")]
    public string? Duration { get; set; }
}

public sealed class FfprobeOutput
{
    [JsonPropertyName("streams")]
    public List<ProbeStreamInfo>? Streams { get; set; }

    [JsonPropertyName("format")]
    public ProbeFormat? Format { get; set; }
}

public sealed class MediaProbeResult
{
    public double DurationSeconds { get; init; }
    public IReadOnlyList<ProbeStreamInfo> Streams { get; init; } = [];

    public bool HasVideoStream => Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
    public bool HasAudioStream => Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
}

public interface IFfprobeClient
{
    Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default);
    Task<IReadOnlyList<double>> GetKeyframePtsAsync(string filePath, CancellationToken ct = default);
    bool ValidateRemux(MediaProbeResult tsProbe, MediaProbeResult remuxProbe, out string? failureReason);
}

public sealed class FfprobeClient : IFfprobeClient
{
    private readonly string _ffprobePath;

    public FfprobeClient(string? ffprobePath = null)
    {
        if (string.IsNullOrWhiteSpace(ffprobePath))
        {
            var paths = new FfmpegLocator().Locate();
            _ffprobePath = paths.FfprobePath;
        }
        else
        {
            _ffprobePath = ffprobePath;
        }
    }

    public async Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
        {
            Log.Warning("Ffprobe: File does not exist: {Path}", filePath);
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            Arguments = $"-v error -show_entries format=duration:stream=codec_type,codec_name,pix_fmt,width,height,sample_rate,channels -of json \"{filePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                Log.Warning("Ffprobe exited with code {Code} for {Path}: {Stderr}", process.ExitCode, filePath, stderr);
                return null;
            }

            var output = JsonSerializer.Deserialize<FfprobeOutput>(stdout);
            if (output == null)
            {
                return null;
            }

            double duration = 0.0;
            if (output.Format?.Duration != null &&
                double.TryParse(output.Format.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration))
            {
                duration = parsedDuration;
            }

            return new MediaProbeResult
            {
                DurationSeconds = duration,
                Streams = output.Streams ?? []
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ffprobe failed on {Path}", filePath);
            return null;
        }
    }

    public async Task<IReadOnlyList<double>> GetKeyframePtsAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            Arguments = $"-v error -select_streams v:0 -skip_frame nokey -show_entries frame=pts_time -of csv=p=0 \"{filePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var keyframeTimes = new List<double>();
        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            using var reader = new StringReader(stdout);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
                {
                    keyframeTimes.Add(pts);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ffprobe GetKeyframePts failed on {Path}", filePath);
        }

        return keyframeTimes;
    }

    public bool ValidateRemux(MediaProbeResult tsProbe, MediaProbeResult remuxProbe, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(tsProbe);
        ArgumentNullException.ThrowIfNull(remuxProbe);

        if (!remuxProbe.HasVideoStream)
        {
            failureReason = "Remux file is missing a video stream.";
            return false;
        }

        if (tsProbe.HasAudioStream && !remuxProbe.HasAudioStream)
        {
            failureReason = "Remux file is missing an audio stream present in original TS.";
            return false;
        }

        // Duration check: within max(2.0s, 2% of TS duration)
        var allowedDelta = Math.Max(2.0, tsProbe.DurationSeconds * 0.02);
        var diff = Math.Abs(remuxProbe.DurationSeconds - tsProbe.DurationSeconds);
        if (tsProbe.DurationSeconds > 1.0 && diff > allowedDelta)
        {
            failureReason = $"Remux duration ({remuxProbe.DurationSeconds:F2}s) differs from TS duration ({tsProbe.DurationSeconds:F2}s) by {diff:F2}s (allowed: {allowedDelta:F2}s).";
            return false;
        }

        failureReason = null;
        return true;
    }
}
