using System.Diagnostics;
using Serilog;

namespace ScreenVault.Core.Ffmpeg;

/// <summary>Runs a short FFmpeg job (remux, merge, clip) and makes sure it never outlives a cancel.</summary>
public static class FfmpegRunner
{
    public static async Task<(int ExitCode, string Stderr)> RunAsync(
        string ffmpegPath,
        IEnumerable<string> arguments,
        ProcessPriorityClass? priority = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        if (priority.HasValue)
        {
            try
            {
                process.PriorityClass = priority.Value;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Not allowed to change the priority: run at the default one.
            }
        }

        // Drain both pipes so FFmpeg can never block on a full buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                // Kill() only requests termination; wait for it to actually happen (with a
                // non-cancellable token, since we've already decided to cancel) so the caller
                // never races FFmpeg for the output file it was writing.
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Debug(ex, "Could not stop FFmpeg after cancellation.");
            }

            // The process exiting closes its redirected streams, which can fault a pending
            // ReadToEndAsync. Observe that fault here instead of leaving it to surface later as an
            // unobserved task exception, then propagate the real OperationCanceledException.
            await ObserveAsync(stdoutTask).ConfigureAwait(false);
            await ObserveAsync(stderrTask).ConfigureAwait(false);
            throw;
        }

        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stderr);

        static async Task ObserveAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Ignoring a stream-read fault after FFmpeg was cancelled and killed.");
            }
        }
    }
}
