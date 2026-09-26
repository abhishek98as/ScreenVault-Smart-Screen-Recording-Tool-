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
    private bool _isDisposed;

    public string DeviceId => _device.ID;
    public string DeviceFriendlyName => _device.FriendlyName;
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
                device.FriendlyName,
                _capture.WaveFormat);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start capture on '{Name}'", device.FriendlyName);
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
            Log.Debug(ex, "Error processing audio data on '{Name}'", _device.FriendlyName);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_isDisposed) return;

        if (e.Exception != null)
        {
            Log.Warning(e.Exception, "Audio capture stopped unexpectedly on '{Name}'", _device.FriendlyName);
            Faulted?.Invoke(this, e.Exception);
        }
        else
        {
            Log.Information("Audio capture stopped on '{Name}'", _device.FriendlyName);
        }
    }

    public bool CheckHealth()
    {
        // For mic, lack of DataAvailable for 3 seconds indicates device fault
        if (!_isLoopback)
        {
            if ((DateTime.UtcNow - _lastDataReceivedUtc).TotalSeconds > 3.0)
            {
                Log.Warning("Mic health check failed: no data for 3s on '{Name}'", _device.FriendlyName);
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
}
