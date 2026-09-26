using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace ScreenVault.Core.Audio;

public enum DeviceEventType
{
    DefaultChanged,
    Added,
    Removed,
    StateChanged,
    SourceFaulted
}

public sealed record DeviceEvent(
    DeviceEventType Type,
    DataFlow? Flow,
    Role? Role,
    string? DeviceId,
    DeviceState? State,
    string? Detail);

public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly Action<DeviceEvent> _enqueue;

    public DeviceWatcher(MMDeviceEnumerator enumerator, Action<DeviceEvent> enqueue)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));

        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
            Log.Information("Registered MMDevice notification client.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register MMDevice notification callback.");
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        _enqueue(new DeviceEvent(DeviceEventType.StateChanged, null, null, deviceId, newState, $"{deviceId} -> {newState}"));

    public void OnDeviceAdded(string pwstrDeviceId) =>
        _enqueue(new DeviceEvent(DeviceEventType.Added, null, null, pwstrDeviceId, null, pwstrDeviceId));

    public void OnDeviceRemoved(string deviceId) =>
        _enqueue(new DeviceEvent(DeviceEventType.Removed, null, null, deviceId, null, deviceId));

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string? defaultDeviceId) =>
        _enqueue(new DeviceEvent(DeviceEventType.DefaultChanged, flow, role, defaultDeviceId, null, $"{flow} {role} -> {defaultDeviceId ?? "null"}"));

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch
        {
            // Ignore error on unregister during shutdown
        }
    }
}
