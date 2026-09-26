using System.Diagnostics;
using ScreenVault.Core.Settings;
using Serilog;
using Vortice.DXGI;

namespace ScreenVault.Core.Ffmpeg;

public sealed record ProfileProbeStatus(
    string ProfileName,
    bool Success,
    int ExitCode,
    string Reason,
    TimeSpan Duration,
    IReadOnlyList<string> LastStderrLines);

public sealed record EncoderProbeResult(
    string ProfileName,
    string Fingerprint,
    IReadOnlyList<ProfileProbeStatus> Details)
{
    // Backwards-compatible 2-arg constructor for tests
    public EncoderProbeResult(string profileName, string fingerprint)
        : this(profileName, fingerprint, Array.Empty<ProfileProbeStatus>())
    {
    }
}

public sealed class EncoderProbe
{
    public static readonly string[] CandidateProfiles =
    [
        "nvenc-d3d11",
        "amf-d3d11",
        "qsv-hwmap",
        "nvenc-sysmem",
        "amf-sysmem",
        "qsv-sysmem",
        "x264"
    ];

    public static EncoderProbeResult? LastResult { get; private set; }

    public static string GetGpuFingerprint()
    {
        try
        {
            DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory);
            if (factory != null)
            {
                using (factory)
                {
                    if (factory.EnumAdapters1(0, out var adapter).Success && adapter != null)
                    {
                        using (adapter)
                        {
                            var desc = adapter.Description;
                            return $"{desc.Description}_{desc.VendorId}_{desc.DeviceId}";
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not query DXGI adapter for GPU fingerprint.");
        }

        return "UnknownGpu";
    }

    public static async Task<EncoderProbeResult> ProbeAsync(string ffmpegPath, CancellationToken ct = default)
    {
        var fingerprint = GetGpuFingerprint();
        Log.Information("Starting encoder probe. GPU Fingerprint: {Fingerprint}", fingerprint);

        var details = new List<ProfileProbeStatus>();
        string? selectedProfile = null;

        foreach (var profileName in CandidateProfiles)
        {
            ct.ThrowIfCancellationRequested();
            var profile = EncoderProfile.Create(profileName, VideoQuality.Balanced, 15);

            var status = await TestProfileWithDiagnosticsAsync(ffmpegPath, profile, ct).ConfigureAwait(false);
            details.Add(status);

            if (status.Success && selectedProfile == null)
            {
                selectedProfile = profileName;
                Log.Information("Encoder probe selected profile: {Profile}", profileName);
            }
        }

        selectedProfile ??= "x264";
        if (selectedProfile == "x264")
        {
            Log.Warning("All hardware encoder probes failed. Falling back to libx264.");
        }

        var result = new EncoderProbeResult(selectedProfile, fingerprint, details);
        LastResult = result;
        return result;
    }

    private static async Task<ProfileProbeStatus> TestProfileWithDiagnosticsAsync(
        string ffmpegPath,
        EncoderProfile profile,
        CancellationToken ct)
    {
        var filter = $"ddagrab=output_idx=0:framerate=15{profile.FilterChainSuffix}";
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(filter);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("3");

        foreach (var arg in profile.EncoderArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        var sw = Stopwatch.StartNew();
        var stderrLines = new List<string>();

        try
        {
            Log.Information("Testing encoder profile '{Profile}' with command: {Cmd}",
                profile.Name,
                string.Join(" ", startInfo.ArgumentList));

            using var process = new Process { StartInfo = startInfo };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    lock (stderrLines)
                    {
                        if (stderrLines.Count >= 20) stderrLines.RemoveAt(0);
                        stderrLines.Add(e.Data.Trim());
                    }
                }
            };

            if (!process.Start())
            {
                return new ProfileProbeStatus(profile.Name, false, -1, "Process failed to start", sw.Elapsed, Array.Empty<string>());
            }

            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();

            var success = process.ExitCode == 0;
            string reason;
            lock (stderrLines)
            {
                if (success)
                {
                    reason = "Supported";
                }
                else
                {
                    var firstError = stderrLines.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                                     ?? stderrLines.FirstOrDefault();
                    reason = firstError ?? $"Exit code {process.ExitCode}";
                }
            }

            Log.Information("Encoder probe '{Profile}' result: Success={Success}, Code={Code}, Duration={Duration:F2}s, Reason={Reason}",
                profile.Name, success, process.ExitCode, sw.Elapsed.TotalSeconds, reason);

            return new ProfileProbeStatus(profile.Name, success, process.ExitCode, reason, sw.Elapsed, stderrLines.ToArray());
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Log.Warning("Encoder probe '{Profile}' timed out after 15s", profile.Name);
            return new ProfileProbeStatus(profile.Name, false, -1, "Timed out (>15s)", sw.Elapsed, stderrLines.ToArray());
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Warning(ex, "Encoder probe for profile '{Profile}' failed with exception", profile.Name);
            return new ProfileProbeStatus(profile.Name, false, -1, ex.Message, sw.Elapsed, stderrLines.ToArray());
        }
    }
}
