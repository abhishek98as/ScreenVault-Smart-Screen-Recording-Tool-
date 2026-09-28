using System.Diagnostics;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.Settings;
using Shouldly;

namespace ScreenVault.IntegrationTests;

/// <summary>
/// Runs the real recording command (with a test pattern instead of the screen) and feeds it audio
/// the way the recorder does, then checks the result has an audible sound track.
/// Uses SV_TEST_FFMPEG when set, otherwise the bundled FFmpeg.
/// </summary>
public sealed class RecordingAudioIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RecordingCommand_RecordsThePipedAudio()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SV_TEST_FFMPEG");
        if (string.IsNullOrEmpty(ffmpeg))
        {
            ffmpeg = new FfmpegLocator().Locate().FfmpegPath;
        }

        var settings = new AppSettings();
        var profile = EncoderProfile.Create("x264", settings.Video.Quality, settings.Video.FrameRate);
        var spec = FfmpegCommandBuilder.Build(ffmpeg, settings, profile, includeAudio: true);
        var args = spec.Arguments
            .Select(a => a.StartsWith("ddagrab=", StringComparison.Ordinal) ? "testsrc2=s=640x360:r=15,realtime" : a)
            .ToList();

        var ts = await RecordAsync(ffmpeg, args, seconds: 4);

        ts.Length.ShouldBeGreaterThan(0);
        var rms = await DecodedAudioRmsAsync(ffmpeg, ts);
        rms.ShouldBeGreaterThan(0.1f, "the -6 dBFS test tone must survive into the recording");
    }

    private static async Task<byte[]> RecordAsync(string ffmpeg, List<string> args, int seconds)
    {
        using var process = Start(ffmpeg, args);
        using var ts = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(ts);
        var stderr = process.StandardError.ReadToEndAsync();

        using (var writer = new StdinWriter())
        {
            writer.AttachStream(process.StandardInput.BaseStream);

            // Same pacing as AudioPump: real time, in chunks of at least 10 ms.
            var clock = Stopwatch.StartNew();
            long written = 0;
            while (written < 48000L * seconds)
            {
                var due = (long)(clock.Elapsed.TotalSeconds * 48000) - written;
                if (due < 480)
                {
                    Thread.Sleep(2);
                    continue;
                }

                var frames = (int)Math.Min(due, 9600);
                var buffer = BufferPool.Floats.Rent(frames * 2);
                for (var i = 0; i < frames; i++)
                {
                    var sample = 0.5f * MathF.Sin(2 * MathF.PI * 440f * (written + i) / 48000f);
                    buffer[i * 2] = sample;
                    buffer[(i * 2) + 1] = sample;
                }

                writer.EnqueueChunk(buffer, frames);
                written += frames;
            }

            await Task.Delay(300);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await stdout;
        var log = await stderr;
        process.ExitCode.ShouldBe(0, log);
        return ts.ToArray();
    }

    private static async Task<float> DecodedAudioRmsAsync(string ffmpeg, byte[] ts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sv_audio_{Guid.NewGuid():N}.ts");
        await File.WriteAllBytesAsync(path, ts);
        try
        {
            using var process = Start(ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-i", path, "-map", "0:a:0", "-ac", "1", "-f", "f32le", "pipe:1"]);
            using var pcm = new MemoryStream();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.BaseStream.CopyToAsync(pcm);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            process.ExitCode.ShouldBe(0, await stderr);

            var bytes = pcm.ToArray();
            var count = bytes.Length / 4;
            count.ShouldBeGreaterThan(48000, "the recording must contain more than a second of audio");
            double sum = 0;
            for (var i = 0; i < count; i++)
            {
                var sample = BitConverter.ToSingle(bytes, i * 4);
                sum += sample * sample;
            }

            return (float)Math.Sqrt(sum / count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Process Start(string ffmpeg, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg did not start.");
    }
}
