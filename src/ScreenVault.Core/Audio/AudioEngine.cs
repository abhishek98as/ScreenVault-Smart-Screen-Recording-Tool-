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

    /// <summary>
    /// Keeps capture (and level meters) running while at least one owner wants it, e.g. the status
    /// window and the settings window independently. Each owner switches only its own request.
    /// </summary>
    void SetMonitoring(object owner, bool enabled) => SetMonitoring(enabled);

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

    private readonly HashSet<object> _monitoringOwners = new(ReferenceEqualityComparer.Instance);
    private static readonly object DefaultMonitoringOwner = new();

    private DeviceWatcher? _watcher;
    private AudioSettings _settings = new();
    private List<string> _currentDegradedFlags = [];
    private volatile CaptureSource[] _sourcesSnapshot = [];
    private volatile bool _started;
    private bool _outputAttached;
    private bool _captureDesired;
    private bool _monitoringEnabled;
    private volatile bool _isMicMuted;
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

    public void SetMonitoring(bool enabled) => SetMonitoring(DefaultMonitoringOwner, enabled);

    public void SetMonitoring(object owner, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _controlThread.Post(() =>
        {
            var changed = enabled ? _monitoringOwners.Add(owner) : _monitoringOwners.Remove(owner);
            var monitoring = _monitoringOwners.Count > 0;
            if (changed && _monitoringEnabled != monitoring)
            {
                Log.Information("AudioEngine monitoring: {Enabled}", monitoring);
                _monitoringEnabled = monitoring;
                ReconcileInternal();
            }
        });
    }

    public void SetMicMute(bool muted)
    {
        // Update the flag immediately so a quick second toggle (or the UI refreshing right after)
        // sees the new state; the gain change itself happens on the audio control thread.
        if (_isMicMuted == muted)
        {
            return;
        }

        _isMicMuted = muted;
        Log.Information("AudioEngine SetMicMute: {Muted}", muted);
        _controlThread.Post(ApplyGroupGains);
    }

    private void ApplyGroupGains()
    {
        var micGainLinear = _isMicMuted ? 0f : Mixer.DbToLinear(_settings.MicGainDb);
        var sysGainLinear = Mixer.DbToLinear(_settings.SystemGainDb);
        foreach (var src in _activeSources.Values)
        {
            src.GroupGainLinear = src.IsLoopback ? sysGainLinear : micGainLinear;
        }
    }

    public void ApplySettings(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _controlThread.Post(() =>
        {
            _settings = settings;
            Log.Information("Applying updated AudioSettings.");
            ApplyGroupGains();
            ReconcileInternal();
        });
    }

    public AudioStatus GetStatus()
    {
        // The source dictionary belongs to the audio control thread; read the published snapshot.
        var sources = _sourcesSnapshot;
        lock (_stateLock)
        {
            var deviceStatuses = sources
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
                    PublishSources([]);
                    foreach (var src in _activeSources.Values)
                    {
                        src.Dispose();
                    }
                    _activeSources.Clear();
                }
                UpdateDisplayStatuses(null);
                return;
            }

            var resolution = EndpointResolver.Resolve(_enumerator, _settings);
            var desiredIds = resolution.DesiredEndpoints.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            var now = DateTime.UtcNow;

            // Remove retry tracking for endpoints no longer desired
            var nonDesiredRetries = _retryTracker.Keys.Where(id => !desiredIds.ContainsKey(id)).ToList();
            foreach (var id in nonDesiredRetries)
            {
                _retryTracker.Remove(id);
            }

            // 1. Remove sources no longer desired, and sources whose capture died (driver reset,
            //    Bluetooth profile switch, exclusive-mode takeover): they are re-created below.
            var toRemove = _activeSources
                .Where(kv => !desiredIds.ContainsKey(kv.Key) || kv.Value.IsFaulted)
                .Select(kv => kv.Key)
                .ToList();
            if (toRemove.Count > 0)
            {
                PublishSources(_activeSources.Where(kv => !toRemove.Contains(kv.Key)).Select(kv => kv.Value).ToArray());
            }

            foreach (var id in toRemove)
            {
                if (_activeSources.Remove(id, out var src))
                {
                    if (src.IsFaulted)
                    {
                        // Back off if the device keeps failing right after being reopened.
                        if (!_retryTracker.TryGetValue(id, out var retry))
                        {
                            retry = new RetryInfo();
                            _retryTracker[id] = retry;
                        }

                        if (now - src.CreatedUtc > TimeSpan.FromMinutes(1))
                        {
                            retry.FailureCount = 0;
                        }

                        retry.FailureCount++;
                        var delayMs = retry.FailureCount switch
                        {
                            1 => 250,
                            2 => 1000,
                            3 => 2000,
                            _ => 10000
                        };
                        retry.NextRetryUtc = now.AddMilliseconds(delayMs);
                        retry.Reason = "capture stopped unexpectedly";
                        ScheduleReconcile(delayMs);
                        Log.Warning("Restarting audio source '{Name}' ({Id}) in {Delay}ms after its capture stopped unexpectedly", src.DeviceFriendlyName, id, delayMs);
                    }
                    else
                    {
                        Log.Information("Removing audio source '{Name}' ({Id})", src.DeviceFriendlyName, id);
                    }

                    src.Dispose();
                    changed = true;
                }
            }

            // 2. Add new desired sources with retry backoff
            var micGainLinear = _isMicMuted ? 0f : Mixer.DbToLinear(_settings.MicGainDb);
            var sysGainLinear = Mixer.DbToLinear(_settings.SystemGainDb);

            foreach (var ep in resolution.DesiredEndpoints)
            {
                if (!_activeSources.ContainsKey(ep.Id))
                {
                    if (_retryTracker.TryGetValue(ep.Id, out var retry) && now < retry.NextRetryUtc)
                    {
                        continue;
                    }

                    NAudio.CoreAudioApi.MMDevice? mmDevice = null;
                    try
                    {
                        mmDevice = _enumerator.GetDevice(ep.Id);
                        var source = new CaptureSource(mmDevice, ep.IsLoopback, _settings.JitterTargetMs)
                        {
                            GroupGainLinear = ep.IsLoopback ? sysGainLinear : micGainLinear
                        };
                        mmDevice = null; // owned by the source now

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
                        if (_retryTracker.TryGetValue(ep.Id, out var prior))
                        {
                            // Keep the failure count (it escalates if the device fails again soon).
                            prior.NextRetryUtc = DateTime.MinValue;
                        }
                        changed = true;
                        Log.Information("Added active audio source '{Name}' ({Id})", ep.Name, ep.Id);
                    }
                    catch (Exception ex)
                    {
                        mmDevice?.Dispose();
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
                        ScheduleReconcile(delayMs);
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
            PublishSources(sourcesArray);

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

    /// <summary>Runs a reconcile after <paramref name="delayMs"/> so retries don't wait for the 5 s safety timer.</summary>
    private void ScheduleReconcile(int delayMs)
    {
        try
        {
            _debounceTimer.Change(delayMs + 10, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void PublishSources(CaptureSource[] sources)
    {
        _sourcesSnapshot = sources;
        _pump.UpdateSources(sources);
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
                    if (resolution == null)
                    {
                        _micDisplayStatus = "✖ Not in use";
                    }
                    else if (desiredMic != null && _retryTracker.TryGetValue(desiredMic.Id, out var retry))
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
                    if (resolution == null)
                    {
                        _systemDisplayStatus = "✖ Not in use";
                    }
                    else if (desiredSys != null && _retryTracker.TryGetValue(desiredSys.Id, out var retry))
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
            PublishSources([]);
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
