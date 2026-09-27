using System.Diagnostics;
using Serilog;

namespace ScreenVault.Core.Ffmpeg;

public static class OrphanProcessKiller
{
    public static int KillOrphans(string expectedFfmpegPath)
    {
        if (string.IsNullOrWhiteSpace(expectedFfmpegPath))
        {
            return 0;
        }

        var expectedFullPath = SafeFullPath(expectedFfmpegPath);

        // Anything started after this instance is ours (a recording or remux job that is running now).
        // Only processes left over from a previous run are orphans.
        DateTime appStartedLocal;
        using (var self = Process.GetCurrentProcess())
        {
            appStartedLocal = self.StartTime;
        }

        var killedCount = 0;
        try
        {
            var processes = Process.GetProcessesByName("ffmpeg");
            foreach (var process in processes)
            {
                try
                {
                    var exePath = process.MainModule?.FileName;
                    if (!string.Equals(SafeFullPath(exePath), expectedFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (process.StartTime >= appStartedLocal)
                    {
                        continue;
                    }

                    Log.Warning("Killing orphan FFmpeg process {Pid} from previous session", process.Id);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                    killedCount++;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not inspect or kill ffmpeg process {Pid}", process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate ffmpeg processes for orphan cleanup");
        }

        return killedCount;
    }

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
