using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.Audio;

public sealed class AudioDeviceSwitchedEventArgs : EventArgs
{
    public string Detail { get; init; } = string.Empty;
}

public sealed record AudioDeviceStatus(string Id, string Name, bool IsLoopback, float PeakDb, float RmsDb);

public sealed record AudioStatus(
    IReadOnlyList<AudioDeviceStatus> ActiveDevices,
    IReadOnlyList<string> DegradedWarnings,
    long DroppedChunks,
    bool IsPumping,
    string LastSwitchSummary = "No device changes yet",
    bool IsMicMuted = false,
    string MicDisplayStatus = "✖ None",
    string SystemDisplayStatus = "✖ None")
{
    public IReadOnlyList<string> DegradedFlags => DegradedWarnings;
}

public interface IAudioEngine : IDisposable
{
    void Start(AudioSettings settings);
    void EnsureCaptureRunning();
    void AttachOutput(Stream ffmpegStdin);
    void DetachOutput();
    void SetMonitoring(bool enabled);
    void SetMicMute(bool muted);
    bool IsMicMuted { get; }
    void ApplySettings(AudioSettings settings);
    AudioStatus GetStatus();
    string LastSwitchSummary { get; }
    event EventHandler<AudioDeviceSwitchedEventArgs>? DeviceSwitched;
}

public sealed class AudioEngine : IAudioEngine
{
    private sealed class RetryInfo
    {
        public int FailureCount { get; set; }
        public DateTime NextRetryUtc { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    private readonly AudioControlThread _controlThread = new();
    private readonly StdinWriter _stdinWriter;
    private readonly AudioPump _pump;
    private readonly Dictionary<string, CaptureSource> _activeSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RetryInfo> _retryTracker = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private MMDeviceEnumerator? _enumerator;
    private readonly ConcurrentQueue<DeviceEvent> _eventQueue = new();
    private readonly System.Threading.Timer _debounceTimer;
    private readonly System.Threading.Timer _safetyTimer;

    private DeviceWatcher? _watcher;
    private AudioSettings _settings = new();
    private List<string> _currentDegradedFlags = [];
    private bool _started;
    private bool _outputAttached;
    private bool _captureDesired;
    private bool _monitoringEnabled;
    private bool _isMicMuted;
    private string _lastSwitchSummary = "No device changes yet";
    private string _micDisplayStatus = "✖ None";
    private string _systemDisplayStatus = "✖ None";

    public bool IsMicMuted => _isMicMuted;
    public string LastSwitchSummary => _lastSwitchSummary;
    public event EventHandler<AudioDeviceSwitchedEventArgs>? DeviceSwitched;

    public AudioEngine()
    {
        _stdinWriter = new StdinWriter();
        _pump = new AudioPump(_stdinWriter);

        _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _safetyTimer = new System.Threading.Timer(OnSafetyTimerElapsed, null, 5000, 5000);
    }

    public void Start(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _controlThread.Send(() =>
        {
            _settings = settings;
            _enumerator ??= new MMDeviceEnumerator();
            _watcher ??= new DeviceWatcher(_enumerator, EnqueueDeviceEvent);
            _started = true;
            Log.Information("AudioEngine started with MicMode={Mic}, OutputMode={Out}",
                settings.MicMode, settings.OutputMode);
            ReconcileInternal();
        });
    }

    public void EnsureCaptureRunning()
    {
        _controlThread.Send(() =>
        {
            _captureDesired = true;
            ReconcileInternal();
        });
    }

    public void AttachOutput(Stream ffmpegStdin)
    {
        ArgumentNullException.ThrowIfNull(ffmpegStdin);
        _controlThread.Send(() =>
        {
            _outputAttached = true;
            _stdinWriter.AttachStream(ffmpegStdin);
            _pump.ResetTimelineAndStart();
            ReconcileInternal();
        });
    }

    public void DetachOutput()
    {
        _controlThread.Send(() =>
        {
            _outputAttached = false;
            _captureDesired = false;
            _pump.StopPumping();
            _stdinWriter.DetachStream();
            ReconcileInternal();
        });
    }

    public void SetMonitoring(bool enabled)
    {
        _controlThread.Post(() =>
        {
            if (_monitoringEnabled != enabled)
            {
                Log.Information("AudioEngine SetMonitoring: {Enabled}", enabled);
                _monitoringEnabled = enabled;
                ReconcileInternal();
            }
        });
    }

    public void SetMicMute(bool muted)
    {
        _controlThread.Post(() =>
        {
            if (_isMicMuted != muted)
            {
                _isMicMuted = muted;
                Log.Information("AudioEngine SetMicMute: {Muted}", muted);
                var micGainLinear = _isMicMuted ? 0f : Mixer.DbToLinear(_settings.MicGainDb);
                foreach (var src in _activeSources.Values.Where(s => !s.IsLoopback))
                {
                    src.GroupGainLinear = micGainLinear;
                }
            }
        });
    }

    public void ApplySettings(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _controlThread.Post(() =>
        {
            _settings = settings;
            Log.Information("Applying updated AudioSettings.");
            var micGainLinear = _isMicMuted ? 0f : Mixer.DbToLinear(_settings.MicGainDb);
            var sysGainLinear = Mixer.DbToLinear(_settings.SystemGainDb);
            foreach (var src in _activeSources.Values)
            {
                src.GroupGainLinear = src.IsLoopback ? sysGainLinear : micGainLinear;
            }
            ReconcileInternal();
        });
    }

    public AudioStatus GetStatus()
    {
        lock (_stateLock)
        {
            var deviceStatuses = _activeSources.Values
                .Select(s => new AudioDeviceStatus(s.DeviceId, s.DeviceFriendlyName, s.IsLoopback, s.Meter.PeakDb, s.Meter.RmsDb))
                .ToList();

            var warnings = new List<string>(_currentDegradedFlags);
            if (DateTime.UtcNow - _controlThread.LastHeartbeatUtc > TimeSpan.FromSeconds(5))
            {
                warnings.Add("AudioControlThread unresponsive for >5s");
            }

            return new AudioStatus(
                deviceStatuses,
                warnings,
                _stdinWriter.DroppedChunks,
                IsPumping: _outputAttached,
                LastSwitchSummary: _lastSwitchSummary,
                IsMicMuted: _isMicMuted,
                MicDisplayStatus: _micDisplayStatus,
                SystemDisplayStatus: _systemDisplayStatus);
        }
    }

    private void EnqueueDeviceEvent(DeviceEvent evt)
    {
        _eventQueue.Enqueue(evt);
        _debounceTimer.Change(400, Timeout.Infinite);
    }

    private void OnDebounceElapsed(object? state)
    {
        _controlThread.Post(() =>
        {
            while (_eventQueue.TryDequeue(out var evt))
            {
                Log.Information("DeviceEvent: {Type} - {Detail}", evt.Type, evt.Detail);
            }
            ReconcileInternal();
        });
    }

    private void OnSafetyTimerElapsed(object? state)
    {
        if (!_started) return;
        _controlThread.Post(ReconcileInternal);
    }

    private void ReconcileInternal()
    {
        if (!_started || _enumerator == null) return;

        try
        {
            // Privacy mode: if neither recording nor monitoring, close capture sources so microphone indicator turns off
            var shouldCapture = _outputAttached || _captureDesired || _monitoringEnabled;
            if (!shouldCapture)
            {
                if (_activeSources.Count > 0)
                {
                    Log.Information("AudioEngine: neither recording nor monitoring. Closing {Count} capture source(s) for privacy.", _activeSources.Count);
                    foreach (var src in _activeSources.Values)
                    {
                        src.Dispose();
                    }
                    _activeSources.Clear();
                    _pump.UpdateSources([]);
                }
                UpdateDisplayStatuses(null);
                return;
            }

            var resolution = EndpointResolver.Resolve(_enumerator, _settings);
            var desiredIds = resolution.DesiredEndpoints.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            var changed = false;

            // Remove retry tracking for endpoints no longer desired
            var nonDesiredRetries = _retryTracker.Keys.Where(id => !desiredIds.ContainsKey(id)).ToList();
            foreach (var id in nonDesiredRetries)
            {
                _retryTracker.Remove(id);
            }

            // 1. Remove sources no longer desired
            var toRemove = _activeSources.Keys.Where(id => !desiredIds.ContainsKey(id)).ToList();
            foreach (var id in toRemove)
            {
                if (_activeSources.Remove(id, out var src))
                {
                    Log.Information("Removing audio source '{Name}' ({Id})", src.DeviceFriendlyName, id);
                    src.Dispose();
                    changed = true;
                }
            }

            // 2. Add new desired sources with retry backoff
            var micGainLinear = _isMicMuted ? 0f : Mixer.DbToLinear(_settings.MicGainDb);
            var sysGainLinear = Mixer.DbToLinear(_settings.SystemGainDb);
            var now = DateTime.UtcNow;

            foreach (var ep in resolution.DesiredEndpoints)
            {
                if (!_activeSources.ContainsKey(ep.Id))
                {
                    if (_retryTracker.TryGetValue(ep.Id, out var retry) && now < retry.NextRetryUtc)
                    {
                        continue;
                    }

                    try
                    {
                        var mmDevice = _enumerator.GetDevice(ep.Id);
                        var source = new CaptureSource(mmDevice, ep.IsLoopback, _settings.JitterTargetMs)
                        {
                            GroupGainLinear = ep.IsLoopback ? sysGainLinear : micGainLinear
                        };

                        source.Faulted += (_, ex) =>
                        {
                            EnqueueDeviceEvent(new DeviceEvent(
                                DeviceEventType.SourceFaulted,
                                ep.IsLoopback ? NAudio.CoreAudioApi.DataFlow.Render : NAudio.CoreAudioApi.DataFlow.Capture,
                                null,
                                ep.Id,
                                null,
                                $"Capture faulted on '{ep.Name}': {ex?.Message}"));
                        };
                        _activeSources[ep.Id] = source;
                        _retryTracker.Remove(ep.Id);
                        changed = true;
                        Log.Information("Added active audio source '{Name}' ({Id})", ep.Name, ep.Id);
                    }
                    catch (Exception ex)
                    {
                        var hr = ex is System.Runtime.InteropServices.COMException comEx ? comEx.ErrorCode : ex.HResult;
                        if (!_retryTracker.TryGetValue(ep.Id, out var r))
                        {
                            r = new RetryInfo();
                            _retryTracker[ep.Id] = r;
                        }
                        r.FailureCount++;
                        var delayMs = r.FailureCount switch
                        {
                            1 => 250,
                            2 => 500,
                            3 => 1000,
                            4 => 2000,
                            _ => 10000
                        };
                        r.NextRetryUtc = now.AddMilliseconds(delayMs);
                        r.Reason = $"0x{hr:X8} ({ex.Message})";
                        Log.Warning(ex, "Could not start audio capture source for '{Name}' ({Id}). HRESULT: 0x{Hr:X8}. Retry in {Delay}ms.",
                            ep.Name, ep.Id, hr, delayMs);
                    }
                }
            }

            lock (_stateLock)
            {
                _currentDegradedFlags = resolution.DegradedWarnings.ToList();
            }

            // Update pump sources array atomically
            var sourcesArray = _activeSources.Values.ToArray();
            _pump.UpdateSources(sourcesArray);

            UpdateDisplayStatuses(resolution);

            if (changed)
            {
                var summary = sourcesArray.Length > 0
                    ? string.Join(" + ", sourcesArray.Select(s => s.DeviceFriendlyName))
                    : "No audio sources active";

                var timeStr = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                _lastSwitchSummary = $"{timeStr} — {summary}";
                Log.Information("Active audio sources changed: [{Summary}]", _lastSwitchSummary);
                DeviceSwitched?.Invoke(this, new AudioDeviceSwitchedEventArgs { Detail = _lastSwitchSummary });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in AudioEngine ReconcileInternal.");
        }
    }

    private void UpdateDisplayStatuses(EndpointResolutionResult? resolution)
    {
        lock (_stateLock)
        {
            // Mic
            if (_settings.MicMode == MicMode.None)
            {
                _micDisplayStatus = "✖ Disabled in settings";
            }
            else
            {
                var activeMic = _activeSources.Values.FirstOrDefault(s => !s.IsLoopback);
                if (activeMic != null)
                {
                    _micDisplayStatus = $"✔ {activeMic.DeviceFriendlyName}";
                }
                else
                {
                    var desiredMic = resolution?.DesiredEndpoints.FirstOrDefault(e => !e.IsLoopback);
                    if (desiredMic != null && _retryTracker.TryGetValue(desiredMic.Id, out var retry))
                    {
                        _micDisplayStatus = $"⟳ Retrying: {retry.Reason}";
                    }
                    else
                    {
                        _micDisplayStatus = "✖ No default device";
                    }
                }
            }

            // System
            if (_settings.OutputMode == OutputMode.None)
            {
                _systemDisplayStatus = "✖ Disabled in settings";
            }
            else
            {
                var activeSys = _activeSources.Values.FirstOrDefault(s => s.IsLoopback);
                if (activeSys != null)
                {
                    _systemDisplayStatus = $"✔ {activeSys.DeviceFriendlyName}";
                }
                else
                {
                    var desiredSys = resolution?.DesiredEndpoints.FirstOrDefault(e => e.IsLoopback);
                    if (desiredSys != null && _retryTracker.TryGetValue(desiredSys.Id, out var retry))
                    {
                        _systemDisplayStatus = $"⟳ Retrying: {retry.Reason}";
                    }
                    else
                    {
                        _systemDisplayStatus = "✖ No default device";
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _debounceTimer.Dispose();
        _safetyTimer.Dispose();

        _controlThread.Send(() =>
        {
            _started = false;
            _watcher?.Dispose();
            foreach (var src in _activeSources.Values)
            {
                src.Dispose();
            }
            _activeSources.Clear();
            _enumerator?.Dispose();
            _enumerator = null;
        });

        _pump.Dispose();
        _stdinWriter.Dispose();
        _controlThread.Dispose();
    }
}
