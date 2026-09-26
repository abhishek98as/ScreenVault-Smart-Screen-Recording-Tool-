using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.Audio;

public sealed record ResolvedEndpoint(string Id, string Name, bool IsLoopback, bool IsDegradedFallback = false);

public sealed record EndpointResolutionResult(
    IReadOnlyList<ResolvedEndpoint> DesiredEndpoints,
    IReadOnlyList<string> DegradedWarnings);

public static class EndpointResolver
{
    private const int E_NOTFOUND = unchecked((int)0x80070490);

    public static EndpointResolutionResult Resolve(MMDeviceEnumerator enumerator, AudioSettings settings)
    {
        var desired = new List<ResolvedEndpoint>();
        var warnings = new List<string>();

        // 1. Resolve Microphone
        if (settings.MicMode != MicMode.None)
        {
            var micResolved = ResolveMicrophone(enumerator, settings, warnings);
            if (micResolved != null)
            {
                desired.Add(micResolved);
            }
        }

        // 2. Resolve Outputs (Loopback)
        if (settings.OutputMode != OutputMode.None)
        {
            var outputsResolved = ResolveOutputs(enumerator, settings, warnings);
            foreach (var outEp in outputsResolved)
            {
                if (!desired.Any(d => d.Id.Equals(outEp.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    desired.Add(outEp);
                }
            }
        }

        return new EndpointResolutionResult(desired, warnings);
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
                var device = enumerator.GetDevice(settings.MicDeviceId);
                if (device is { State: DeviceState.Active })
                {
                    return new ResolvedEndpoint(device.ID, device.FriendlyName, false);
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
            var defaultMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
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
                var altMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, altRole);
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
        List<string> warnings)
    {
        var results = new List<ResolvedEndpoint>();

        if (settings.OutputMode == OutputMode.Specific && !string.IsNullOrWhiteSpace(settings.OutputDeviceId))
        {
            try
            {
                var device = enumerator.GetDevice(settings.OutputDeviceId);
                if (device is { State: DeviceState.Active })
                {
                    results.Add(new ResolvedEndpoint(device.ID, device.FriendlyName, true));
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
                    results.Add(new ResolvedEndpoint(dev.ID, dev.FriendlyName, true));
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
            var defMultimedia = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
                var defComms = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
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

        return results;
    }
}
