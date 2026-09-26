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

        var killedCount = 0;
        try
        {
            var processes = Process.GetProcessesByName("ffmpeg");
            foreach (var process in processes)
            {
                try
                {
                    var exePath = process.MainModule?.FileName;
                    if (string.Equals(exePath, expectedFfmpegPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warning("Killing orphan FFmpeg process {Pid} from previous session", process.Id);
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(1000);
                        killedCount++;
                    }
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
}
