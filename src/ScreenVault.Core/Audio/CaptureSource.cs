using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace ScreenVault.Core.Audio;

public sealed class CaptureSource : IDisposable
{
    private readonly MMDevice _device;
    private readonly bool _isLoopback;
    private readonly WasapiCapture _capture;
    private readonly FormatPipeline _pipeline;
    private readonly JitterBuffer _jitterBuffer;
    private readonly LevelMeter _meter = new();

    private DateTime _lastDataReceivedUtc = DateTime.UtcNow;
    private volatile bool _isDisposed;
    private volatile bool _isFaulted;

    public string DeviceId { get; }
    public string DeviceFriendlyName { get; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    /// <summary>True once capture stopped on its own (device invalidated, driver reset…).</summary>
    public bool IsFaulted => _isFaulted;
    public bool IsLoopback => _isLoopback;
    public JitterBuffer Jitter => _jitterBuffer;
    public LevelMeter Meter => _meter;
    public float GroupGainLinear { get; set; } = 1.0f;

    public event EventHandler<Exception?>? Faulted;

    public CaptureSource(MMDevice device, bool isLoopback, int jitterTargetMs = 100)
    {
        _device = device;
        _isLoopback = isLoopback;
        _jitterBuffer = new JitterBuffer(jitterTargetMs);

        // Read once: the COM properties are not reliable after the device has been invalidated.
        DeviceId = device.ID;
        DeviceFriendlyName = SafeFriendlyName(device);

        if (isLoopback)
        {
            _capture = new WasapiLoopbackCapture(device);
        }
        else
        {
            _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
        }

        _pipeline = new FormatPipeline(_capture.WaveFormat);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        try
        {
            _capture.StartRecording();
            Log.Information("Started capture on {Type} device '{Name}' ({Format})",
                isLoopback ? "Loopback" : "Mic",
                DeviceFriendlyName,
                _capture.WaveFormat);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start capture on '{Name}'", DeviceFriendlyName);
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _lastDataReceivedUtc = DateTime.UtcNow;
        try
        {
            _pipeline.PushData(e.Buffer, 0, e.BytesRecorded, _jitterBuffer, _meter);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error processing audio data on '{Name}'", DeviceFriendlyName);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_isDisposed) return;

        // Any stop we did not ask for (with or without an error) leaves this source silent for good.
        _isFaulted = true;
        if (e.Exception != null)
        {
            Log.Warning(e.Exception, "Audio capture stopped unexpectedly on '{Name}'", DeviceFriendlyName);
        }
        else
        {
            Log.Warning("Audio capture stopped on '{Name}' without being asked to", DeviceFriendlyName);
        }

        Faulted?.Invoke(this, e.Exception);
    }

    public bool CheckHealth()
    {
        // For mic, lack of DataAvailable for 3 seconds indicates device fault
        if (!_isLoopback)
        {
            if ((DateTime.UtcNow - _lastDataReceivedUtc).TotalSeconds > 3.0)
            {
                Log.Warning("Mic health check failed: no data for 3s on '{Name}'", DeviceFriendlyName);
                return false;
            }
        }
        // For loopback, silence is normal when no audio is playing
        return true;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.StopRecording();
        }
        catch
        {
            // Ignore error on stop
        }

        _capture.Dispose();
        _device.Dispose();
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return "Audio device";
        }
    }
}
