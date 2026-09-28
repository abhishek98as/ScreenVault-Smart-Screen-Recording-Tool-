using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.Audio;

public sealed record ResolvedEndpoint(string Id, string Name, bool IsLoopback, bool IsDegradedFallback = false, string Reason = "Windows default");

public sealed record EndpointResolutionResult(
    IReadOnlyList<ResolvedEndpoint> DesiredEndpoints,
    IReadOnlyList<string> DegradedWarnings);

public static class EndpointResolver
{
    private const int E_NOTFOUND = unchecked((int)0x80070490);

    /// <summary>
    /// Processes whose microphone or speaker use means "a call" (Teams, Zoom, browsers for Meet or
    /// web Teams, Slack, Discord, Webex…). Background audio tools that keep a device open all the
    /// time (noise suppression, mixers) are deliberately absent: following them would record the
    /// wrong or a doubled microphone. An app not listed simply falls back to the Windows default.
    /// </summary>
    private static readonly HashSet<string> CallApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams", "msteams", "teams", "msedgewebview2", "zoom", "slack", "discord", "discordptb", "discordcanary",
        "skype", "webex", "webexmta", "atmgr", "ciscocollabhost", "ciscowebexstart", "g2mcomm", "gotomeeting", "goto",
        "ringcentral", "whatsapp", "telegram", "signal", "viber", "chime", "lark", "feishu", "bluejeans", "element",
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"
    };

    private static readonly ConcurrentDictionary<uint, (string Name, long ExpiresAt)> ProcessNames = new();

    /// <param name="keepLoopbackIds">
    /// Output devices already recorded in this session. In the recommended output mode they stay
    /// recorded while they exist, so a call or a browser tab that goes quiet for a moment (and
    /// closes its audio stream) doesn't lose the first seconds when it plays again.
    /// </param>
    public static EndpointResolutionResult Resolve(
        MMDeviceEnumerator enumerator,
        AudioSettings settings,
        IReadOnlyCollection<string>? keepLoopbackIds = null)
    {
        var desired = new List<ResolvedEndpoint>();
        var warnings = new List<string>();

        // 1. Resolve Microphone(s)
        if (settings.MicMode != MicMode.None)
        {
            foreach (var mic in ResolveMicrophones(enumerator, settings, warnings))
            {
                AddUnique(desired, mic);
            }
        }

        // 2. Resolve Outputs (Loopback)
        if (settings.OutputMode != OutputMode.None)
        {
            foreach (var outEp in ResolveOutputs(enumerator, settings, warnings, keepLoopbackIds))
            {
                AddUnique(desired, outEp);
            }
        }

        return new EndpointResolutionResult(desired, warnings);
    }

    /// <summary>
    /// In the recommended mode, record the microphone(s) a call app (Teams, Zoom, a browser…) is
    /// capturing from right now: that is what the call hears, and it is often not the Windows
    /// default — a headset or webcam mic, or a docked laptop whose built-in mic is off with the
    /// lid closed. With no call going on, record the Windows default microphone.
    /// </summary>
    private static List<ResolvedEndpoint> ResolveMicrophones(
        MMDeviceEnumerator enumerator,
        AudioSettings settings,
        List<string> warnings)
    {
        if (settings.MicMode == MicMode.DefaultCommunications)
        {
            var inUse = FindEndpointsInUseByCallApps(enumerator, DataFlow.Capture);
            if (inUse.Count > 0)
            {
                return inUse;
            }
        }

        var mic = ResolveMicrophone(enumerator, settings, warnings);
        return mic == null ? [] : [mic];
    }

    /// <summary>
    /// Active <paramref name="flow"/> devices on which a call app (see <see cref="CallApps"/>) is
    /// streaming right now.
    /// </summary>
    public static List<ResolvedEndpoint> FindEndpointsInUseByCallApps(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        var result = new List<ResolvedEndpoint>();
        var ownProcessId = (uint)Environment.ProcessId;
        var isLoopback = flow == DataFlow.Render;

        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    var app = FindActiveCallApp(device, ownProcessId);
                    if (app != null)
                    {
                        result.Add(new ResolvedEndpoint(device.ID, device.FriendlyName, isLoopback, Reason: $"in use by {app}"));
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not read the audio sessions of a {Flow} device.", flow);
                }
                finally
                {
                    try
                    {
                        device.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Could not release a {Flow} device.", flow);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not list {Flow} devices to find the ones in use.", flow);
        }

        return result;
    }

    /// <summary>Name of a call app with an active stream on <paramref name="device"/>, or null.</summary>
    private static string? FindActiveCallApp(MMDevice device, uint ownProcessId)
    {
        var sessions = device.AudioSessionManager.Sessions;
        if (sessions == null)
        {
            return null;
        }

        var count = sessions.Count;
        for (var i = 0; i < count; i++)
        {
            var session = sessions[i];
            try
            {
                if (session.State != AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession)
                {
                    continue;
                }

                var processId = session.GetProcessID;
                if (processId == 0 || processId == ownProcessId)
                {
                    continue;
                }

                var name = ProcessName(processId);
                if (name != null && CallApps.Contains(name))
                {
                    return name;
                }
            }
            finally
            {
                session.Dispose();
            }
        }

        return null;
    }

    /// <summary>Process name for a session, cached for a minute (the lookup scans all processes).</summary>
    private static string? ProcessName(uint processId)
    {
        var now = Environment.TickCount64;
        if (ProcessNames.TryGetValue(processId, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Name;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            if (ProcessNames.Count > 512)
            {
                ProcessNames.Clear();
            }

            ProcessNames[processId] = (name, now + 60_000);
            return name;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // Exited, or not visible to us.
        }
    }

    private static void AddUnique(List<ResolvedEndpoint> list, ResolvedEndpoint endpoint)
    {
        if (!list.Any(e => e.Id.Equals(endpoint.Id, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(endpoint);
        }
    }

    private static ResolvedEndpoint? ResolveMicrophone(
        MMDeviceEnumerator enumerator,
        AudioSettings settings,
        List<string> warnings)
    {
        if (settings.MicMode == MicMode.Specific && !string.IsNullOrWhiteSpace(settings.MicDeviceId))
        {
            try
            {
                using var device = enumerator.GetDevice(settings.MicDeviceId);
                if (device is { State: DeviceState.Active })
                {
                    return new ResolvedEndpoint(device.ID, device.FriendlyName, false, Reason: "chosen in Settings");
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not open pinned microphone {Id}", settings.MicDeviceId);
            }

            warnings.Add("Pinned microphone not available; falling back to default communications microphone.");
        }

        // Default communications or multimedia
        var role = settings.MicMode == MicMode.DefaultMultimedia ? Role.Multimedia : Role.Communications;
        try
        {
            using var defaultMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            if (defaultMic != null)
            {
                var isFallback = settings.MicMode == MicMode.Specific;
                return new ResolvedEndpoint(defaultMic.ID, defaultMic.FriendlyName, false, isFallback);
            }
        }
        catch (COMException comEx) when (comEx.ErrorCode == E_NOTFOUND)
        {
            Log.Debug("No default microphone for role {Role} (HRESULT 0x80070490). Checking fallback role.", role);
            var altRole = role == Role.Communications ? Role.Multimedia : Role.Communications;
            try
            {
                using var altMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, altRole);
                if (altMic != null)
                {
                    return new ResolvedEndpoint(altMic.ID, altMic.FriendlyName, false, true);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Fallback microphone role check also failed.");
                warnings.Add("No default capture device available.");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"No default capture device available ({ex.Message}).");
        }

        return null;
    }

    private static List<ResolvedEndpoint> ResolveOutputs(
        MMDeviceEnumerator enumerator,
        AudioSettings settings,
        List<string> warnings,
        IReadOnlyCollection<string>? keepLoopbackIds)
    {
        var results = new List<ResolvedEndpoint>();

        if (settings.OutputMode == OutputMode.Specific && !string.IsNullOrWhiteSpace(settings.OutputDeviceId))
        {
            try
            {
                using var device = enumerator.GetDevice(settings.OutputDeviceId);
                if (device is { State: DeviceState.Active })
                {
                    results.Add(new ResolvedEndpoint(device.ID, device.FriendlyName, true, Reason: "chosen in Settings"));
                    return results;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not open pinned output device {Id}", settings.OutputDeviceId);
            }

            warnings.Add("Pinned output device not available; falling back to default system audio.");
        }

        if (settings.OutputMode == OutputMode.AllActive)
        {
            try
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var dev in devices)
                {
                    using (dev)
                    {
                        results.Add(new ResolvedEndpoint(dev.ID, dev.FriendlyName, true, Reason: "all playback devices"));
                    }
                }
                return results;
            }
            catch (Exception ex)
            {
                warnings.Add($"Error enumerating all active render devices ({ex.Message}).");
            }
        }

        // Default or DefaultPlusCommunications
        try
        {
            using var defMultimedia = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defMultimedia != null)
            {
                results.Add(new ResolvedEndpoint(defMultimedia.ID, defMultimedia.FriendlyName, true));
            }
        }
        catch (COMException comEx) when (comEx.ErrorCode == E_NOTFOUND)
        {
            Log.Debug("No default multimedia audio output available (HRESULT 0x80070490).");
        }
        catch (Exception ex)
        {
            warnings.Add($"No default multimedia audio output available ({ex.Message}).");
        }

        if (settings.OutputMode == OutputMode.DefaultPlusCommunications)
        {
            try
            {
                using var defComms = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                if (defComms != null && !results.Any(r => r.Id.Equals(defComms.ID, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(new ResolvedEndpoint(defComms.ID, defComms.FriendlyName, true));
                }
            }
            catch (COMException comEx) when (comEx.ErrorCode == E_NOTFOUND)
            {
                Log.Debug("No separate communications audio output endpoint found (HRESULT 0x80070490).");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "No separate communications audio output endpoint found.");
            }
        }

        if (settings.OutputMode != OutputMode.DefaultPlusCommunications)
        {
            return results;
        }

        // Also record wherever a call app is playing right now: a call on a headset, or on the
        // laptop speakers while the Windows default is a monitor that has no speakers.
        foreach (var inUse in FindEndpointsInUseByCallApps(enumerator, DataFlow.Render))
        {
            AddUnique(results, inUse);
        }

        if (keepLoopbackIds != null)
        {
            foreach (var id in keepLoopbackIds)
            {
                if (results.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    using var device = enumerator.GetDevice(id);
                    if (device is { State: DeviceState.Active })
                    {
                        results.Add(new ResolvedEndpoint(device.ID, device.FriendlyName, true, Reason: "used earlier in this recording"));
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Output device {Id} used earlier is gone.", id);
                }
            }
        }

        return results;
    }
}
