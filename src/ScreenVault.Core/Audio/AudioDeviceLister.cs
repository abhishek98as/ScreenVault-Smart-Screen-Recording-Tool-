using NAudio.CoreAudioApi;
using Serilog;

namespace ScreenVault.Core.Audio;

/// <summary>One selectable audio endpoint, as shown in a Settings device picker.</summary>
public sealed record AudioDeviceOption(string Id, string Name);

/// <summary>Lists active Windows audio endpoints for the Settings UI's device pickers.</summary>
public static class AudioDeviceLister
{
    public static IReadOnlyList<AudioDeviceOption> ListCaptureDevices() => List(DataFlow.Capture);

    public static IReadOnlyList<AudioDeviceOption> ListRenderDevices() => List(DataFlow.Render);

    private static List<AudioDeviceOption> List(DataFlow flow)
    {
        var result = new List<AudioDeviceOption>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    result.Add(new AudioDeviceOption(device.ID, device.FriendlyName));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not enumerate {Flow} audio devices for the device picker.", flow);
        }

        return result;
    }
}
