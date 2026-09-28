using System.Globalization;
using ScreenVault.Core.Settings;

namespace ScreenVault.Core.Ffmpeg;

public sealed record FfmpegLaunchSpec(string ExecutablePath, IReadOnlyList<string> Arguments);

public static class FfmpegCommandBuilder
{
    public static FfmpegLaunchSpec Build(
        string ffmpegPath,
        AppSettings settings,
        EncoderProfile profile,
        bool includeAudio = true)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "level+warning",
            "-stats_period", "2",
            "-progress", "pipe:2"
        };

        // Screen capture video filter configuration
        var drawMouse = settings.Video.CaptureCursor ? "1" : "0";
        var filter = $"ddagrab=output_idx={settings.Video.MonitorIndex}:framerate={settings.Video.FrameRate}:draw_mouse={drawMouse}";

        if (settings.Video.DownscaleTo1080p)
        {
            // System-memory scaling filter if downscale requested. Only ever shrink: screens narrower
            // than 1920 px keep their size (scaling them up would just waste space). Width stays even.
            filter += ",hwdownload,format=bgra,scale=w='trunc(min(1920,iw)/2)*2':h=-2";
        }
        else
        {
            filter += profile.FilterChainSuffix;
        }

        args.Add("-f");
        args.Add("lavfi");
        args.Add("-i");
        args.Add(filter);

        if (includeAudio)
        {
            var avOffsetSec = (settings.Audio.AvOffsetMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            args.Add("-itsoffset");
            args.Add(avOffsetSec);
            args.Add("-f");
            args.Add("f32le");
            args.Add("-ar");
            args.Add("48000");
            args.Add("-ac");
            args.Add("2");
            // No -thread_queue_size here: newer FFmpeg builds only accept it as an output option
            // and refuse to start with it on an input, and 9.x records this input cleanly without it.
            args.Add("-i");
            args.Add("pipe:0");

            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add("1:a:0");
        }
        else
        {
            args.Add("-map");
            args.Add("0:v:0");
        }

        // Add video encoder arguments
        args.AddRange(profile.EncoderArgs);

        if (includeAudio)
        {
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("128k");
            args.Add("-ar");
            args.Add("48000");
            args.Add("-shortest");
        }

        args.Add("-flush_packets");
        args.Add("1");
        args.Add("-f");
        args.Add("mpegts");
        args.Add("pipe:1");

        return new FfmpegLaunchSpec(ffmpegPath, args);
    }
}
