using ScreenVault.Core.Audio;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.Output;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using Serilog;

namespace ScreenVault.Core.Recording;

public sealed class RecordingController : IRecordingController, IAsyncDisposable
{
    // FFmpeg that is running but has produced no output for this long after starting is stuck.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DiskStallTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoAudioTimeout = TimeSpan.FromSeconds(10);

    private readonly ISettingsService _settingsService;
    private readonly IFfmpegHost _ffmpegHost;
    private readonly IAudioEngine _audioEngine;
    private readonly ISegmentSink _segmentSink;
    private readonly IClock _clock;
    private readonly ISessionStore? _sessionStore;
    private readonly IMarkerService? _markerService;
    private readonly IPostProcessor? _postProcessor;
    private readonly IStorageManager? _storageManager;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Backoff _backoff = new();
    private readonly System.Threading.Timer _reconcileTimer;
    private readonly SessionClock _sessionClock;
    private readonly List<DateTime> _memoryRestarts = new();

    private volatile RecorderState _state = RecorderState.Idle;
    private volatile DesiredState _desired = DesiredState.Stopped;
    private volatile bool _disposed;
    private DateTime _sessionStartTimeUtc;
    private DateTime _sessionStartLocal;
    private bool _displayChangePending;
    private DateTime _displayChangeRequestedUtc;
    private int _encoderFailures;
    private string _activeProfileName = "x264";
    private volatile SessionManifest? _activeManifest;
    private string? _activeMetadataBackupDir;
    private volatile bool _manifestSavedInitial;
    private int _currentPartIndex = 1;

    // Watchdog state (reconcile thread only)
    private DateTime _pipelineStartedUtc;
    private bool _memoryHighWarning;
    private DateTime? _lowSpeedSinceUtc;
    private DateTime _lastWatchdogLogUtc;
    private DateTime _lastPauseReminderUtc;
    private long _lastBytesWritten;
    private DateTime _lastBytesGrowthUtc;
    private DateTime _lastDiskSizeCheckUtc;
    private volatile bool _diskLengthMismatchWarning;
    private DateTime? _noAudioSinceUtc;
    private bool _noAudioToastShown;

    public RecorderState State => _state;
    public DesiredState Desired => _desired;
    public HealthSnapshot Health => BuildHealthSnapshot();
    public SessionClock Clock => _sessionClock;
    public bool IsMicMuted => _audioEngine.IsMicMuted;

    public event EventHandler<HealthSnapshot>? HealthChanged;
    public event EventHandler? PauseReminderTriggered;
    public event EventHandler<int>? FpsDegradedNotification;
    public event EventHandler? NoAudioSourcesNotification;
    public event EventHandler? DiskWriteStallNotification;
    public event EventHandler<string>? AudioDeviceSwitchedNotification;
    public event EventHandler<SessionManifest>? SessionCompleted;

    public RecordingController(
        ISettingsService settingsService,
        IFfmpegHost ffmpegHost,
        IAudioEngine audioEngine,
        ISegmentSink segmentSink,
        IClock? clock = null,
        ISessionStore? sessionStore = null,
        IMarkerService? markerService = null,
        IPostProcessor? postProcessor = null,
        IStorageManager? storageManager = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _ffmpegHost = ffmpegHost ?? throw new ArgumentNullException(nameof(ffmpegHost));
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        _segmentSink = segmentSink ?? throw new ArgumentNullException(nameof(segmentSink));
        _clock = clock ?? SystemClock.Instance;
        _sessionClock = new SessionClock(_clock);
        _sessionStore = sessionStore;
        _markerService = markerService;
        _postProcessor = postProcessor;
        _storageManager = storageManager;

        _ffmpegHost.Exited += (_, e) =>
        {
            Log.Warning("Controller detected FFmpeg exit (Code: {Code})", e.ExitCode);
            TriggerReconcile();
        };

        _segmentSink.SegmentClosed += OnSegmentClosed;
        _audioEngine.DeviceSwitched += OnAudioDeviceSwitched;

        if (_storageManager != null)
        {
            _storageManager.SwitchRequested += OnStorageSwitchRequested;
        }

        _reconcileTimer = new System.Threading.Timer(_ => TriggerReconcile(), null, 1000, 1000);
    }

    public void TriggerDisplayChanged()
    {
        _displayChangePending = true;
        _displayChangeRequestedUtc = DateTime.UtcNow;
        Log.Information("Display change pending. Will restart pipeline after debounce.");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Log.Information("Start requested by user / launch flag.");
            _desired = DesiredState.Recording;
            _backoff.Reset();
            await ReconcileLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Log.Information("Stop requested by user.");
            _desired = DesiredState.Stopped;
            await ReconcileLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_desired == DesiredState.Stopped)
            {
                Log.Information("Pause ignored: nothing is being recorded.");
                return;
            }

            Log.Information("Pause requested by user.");
            _desired = DesiredState.Paused;
            await ReconcileLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_desired != DesiredState.Paused)
            {
                // Resuming must never start a brand-new recording nobody asked for.
                Log.Information("Resume ignored: recording is not paused (desired state {Desired}).", _desired);
                return;
            }

            Log.Information("Resume requested by user.");
            _desired = DesiredState.Recording;
            _backoff.Reset();
            await ReconcileLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void AddMarker(string? note, string kind = "User")
    {
        var manifest = _activeManifest;
        if (manifest == null)
        {
            Log.Information("Marker '{Note}' ignored: no recording is active.", note);
            return;
        }

        if (_markerService != null)
        {
            _markerService.AddMarker(note ?? string.Empty, kind);
            return;
        }

        var entry = new MarkerEntry
        {
            AtUtc = _clock.UtcNow,
            OffsetSec = Math.Round(_sessionClock.Elapsed.TotalSeconds, 2),
            Note = note ?? string.Empty,
            Kind = kind
        };

        lock (manifest)
        {
            manifest.Markers.Add(entry);
        }

        Log.Information("Marker added: {Kind} - {Note} at {Elapsed}s", kind, note, entry.OffsetSec);
        if (_manifestSavedInitial)
        {
            SaveManifest(manifest);
        }
    }

    public void ToggleMicMute() => SetMicMute(!_audioEngine.IsMicMuted);

    public void SetMicMute(bool muted)
    {
        _audioEngine.SetMicMute(muted);
        var label = muted ? "Mic muted" : "Mic unmuted";
        Log.Information("Mic mute set to {Muted}", muted);

        if (_activeManifest != null)
        {
            AddSessionEvent(muted ? "MicMuted" : "MicUnmuted", label);
            _markerService?.AddMarker(label, "System");
        }
    }

    private void OnAudioDeviceSwitched(object? sender, AudioDeviceSwitchedEventArgs e)
    {
        Log.Information("Controller: Audio device switched - {Detail}", e.Detail);
        AddSessionEvent("AudioDeviceSwitched", e.Detail);
        AudioDeviceSwitchedNotification?.Invoke(this, e.Detail);
    }

    private void OnStorageSwitchRequested(object? sender, StorageSwitchRequestedEventArgs args)
    {
        Log.Information("StorageManager requested switch to {Location}. Reason: {Reason}", args.NewLocation, args.Reason);
        if (args.RequiresRotation)
        {
            _segmentSink.RequestRotation(RotationReason.StorageSwitch);
        }

        AddSessionEvent("StorageSwitched", $"Switched to {args.NewLocation} ({args.Reason})");
    }

    private void TriggerReconcile()
    {
        if (_disposed || _lock.CurrentCount == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (await _lock.WaitAsync(0).ConfigureAwait(false))
                {
                    try
                    {
                        await ReconcileLockedAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        _lock.Release();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        });
    }

    private async Task ReconcileLockedAsync()
    {
        try
        {
            switch (_desired, _state)
            {
                // Not running but should be. Transitional states only remain after an error.
                case (DesiredState.Recording, RecorderState.Idle):
                case (DesiredState.Recording, RecorderState.Faulted):
                case (DesiredState.Recording, RecorderState.Recovering):
                case (DesiredState.Recording, RecorderState.Stopping):
                case (DesiredState.Recording, RecorderState.Saving):
                case (DesiredState.Recording, RecorderState.Suspended):
                    if (_backoff.IsBackoffElapsed())
                    {
                        // Continue the same session (new part) if it already recorded something.
                        if (HasSessionData)
                        {
                            await ResumePipelineAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            await StartPipelineAsync().ConfigureAwait(false);
                        }
                    }
                    break;

                case (DesiredState.Recording, RecorderState.Paused):
                    if (_backoff.IsBackoffElapsed())
                    {
                        await ResumePipelineAsync().ConfigureAwait(false);
                    }
                    break;

                case (DesiredState.Recording, RecorderState.Recording):
                case (DesiredState.Recording, RecorderState.Starting):
                    await SuperviseRunningPipelineAsync().ConfigureAwait(false);
                    break;

                case (DesiredState.Paused, RecorderState.Recording):
                case (DesiredState.Paused, RecorderState.Starting):
                case (DesiredState.Paused, RecorderState.Faulted):
                case (DesiredState.Paused, RecorderState.Recovering):
                case (DesiredState.Paused, RecorderState.Stopping):
                case (DesiredState.Paused, RecorderState.Saving):
                case (DesiredState.Paused, RecorderState.Suspended):
                    await PausePipelineAsync().ConfigureAwait(false);
                    break;

                case (DesiredState.Paused, RecorderState.Idle):
                    if (HasSessionData)
                    {
                        SetState(RecorderState.Paused);
                        _lastPauseReminderUtc = _clock.UtcNow;
                    }
                    else
                    {
                        // Nothing was recorded yet (e.g. the start failed): there is nothing to pause.
                        DiscardSession();
                        _desired = DesiredState.Stopped;
                    }
                    break;

                case (DesiredState.Paused, RecorderState.Paused):
                    // 10-minute pause reminder check
                    if ((_clock.UtcNow - _lastPauseReminderUtc).TotalMinutes >= 10.0)
                    {
                        _lastPauseReminderUtc = _clock.UtcNow;
                        Log.Information("10-minute pause reminder triggered.");
                        PauseReminderTriggered?.Invoke(this, EventArgs.Empty);
                    }
                    break;

                case (DesiredState.Stopped, not RecorderState.Idle):
                    await StopPipelineAsync().ConfigureAwait(false);
                    SetState(RecorderState.Idle);
                    break;
            }

            // If starting, transition to recording once first stdout bytes arrive
            if (_state == RecorderState.Starting && _ffmpegHost.IsRunning && _ffmpegHost.StdoutBytesTotal > 0)
            {
                SetState(RecorderState.Recording);
                _sessionClock.StartSpan();
                _backoff.RecordSuccess();
            }

            // FILE-02: Create manifest once the first segment has bytes on disk
            var manifest = _activeManifest;
            if (!_manifestSavedInitial && manifest != null && _segmentSink.BytesWritten > 0)
            {
                _manifestSavedInitial = true;
                SaveManifest(manifest);
                Log.Information("Initial session manifest saved to disk for session {SessionId}", manifest.SessionId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in RecordingController ReconcileLockedAsync.");
            _backoff.RecordFailure();

            // Never stay in a transitional state after an error, or the state machine stops acting.
            if (_state is RecorderState.Starting or RecorderState.Stopping or RecorderState.Saving or RecorderState.Recovering)
            {
                SetState(_desired == DesiredState.Paused ? RecorderState.Paused : RecorderState.Faulted);
            }
            else if (_backoff.ConsecutiveFailures >= 3 && _desired == DesiredState.Recording)
            {
                SetState(RecorderState.Faulted);
            }
        }

        try
        {
            HealthChanged?.Invoke(this, BuildHealthSnapshot());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A HealthChanged handler failed.");
        }
    }

    private async Task SuperviseRunningPipelineAsync()
    {
        var now = _clock.UtcNow;

        // Check if FFmpeg exited unexpectedly
        if (!_ffmpegHost.IsRunning)
        {
            Log.Warning("Reconcile: FFmpeg stopped while DesiredState is Recording (State={State}). Restarting pipeline.", _state);
            await RestartPipelineAsync("FfmpegExited", countsAsFailure: true).ConfigureAwait(false);
            return;
        }

        var hasOutput = _ffmpegHost.StdoutBytesTotal > 0;
        if (hasOutput)
        {
            // Check for hang (no stdout while running)
            var silentSec = (now - _ffmpegHost.LastStdoutActivityUtc).TotalSeconds;
            if (silentSec > HangTimeout.TotalSeconds)
            {
                Log.Warning("Reconcile: FFmpeg hang detected (no stdout bytes for {Sec:F1}s). Killing and restarting.", silentSec);
                _ffmpegHost.Kill();
                await RestartPipelineAsync("FfmpegHung", countsAsFailure: true).ConfigureAwait(false);
                return;
            }
        }
        else if (now - _pipelineStartedUtc > StartTimeout)
        {
            Log.Warning("Reconcile: FFmpeg produced no output within {Sec:F0}s of starting. Killing and restarting.", StartTimeout.TotalSeconds);
            _ffmpegHost.Kill();
            await RestartPipelineAsync("FfmpegStartTimeout", countsAsFailure: true).ConfigureAwait(false);
            return;
        }

        // Check for pending debounced display change (2s debounce)
        if (_displayChangePending && (DateTime.UtcNow - _displayChangeRequestedUtc).TotalSeconds >= 2.0)
        {
            _displayChangePending = false;
            Log.Information("Reconcile: Applying debounced display change.");
            await RestartPipelineAsync("DisplayChanged", countsAsFailure: false).ConfigureAwait(false);
            return;
        }

        if (_state == RecorderState.Recording)
        {
            await RunRecordingWatchdogsAsync(now).ConfigureAwait(false);
        }
    }

    private async Task RunRecordingWatchdogsAsync(DateTime now)
    {
        var settings = _settingsService.Current;
        var advanced = settings.Advanced;
        var warnMb = advanced.FfmpegMemoryWarnMb > 0 ? advanced.FfmpegMemoryWarnMb : 400;
        var restartMb = advanced.FfmpegMemoryRestartMb > warnMb ? advanced.FfmpegMemoryRestartMb : Math.Max(600, warnMb + 100);
        var speedThreshold = advanced.SpeedDegradeThreshold is > 0 and < 1 ? advanced.SpeedDegradeThreshold : 0.95;
        var speedSeconds = advanced.SpeedDegradeSeconds > 0 ? advanced.SpeedDegradeSeconds : 20;

        // Memory
        var wsMb = _ffmpegHost.WorkingSet64 / (1024.0 * 1024.0);
        _memoryHighWarning = wsMb > warnMb;
        if (wsMb > restartMb)
        {
            Log.Warning("Watchdog: FFmpeg memory {WsMb:F1} MB exceeded {Limit} MB threshold. Triggering restart.", wsMb, restartMb);
            AddSessionEvent("FfmpegMemoryRestart", $"Working set reached {wsMb:F1} MB");

            _memoryRestarts.Add(now);
            _memoryRestarts.RemoveAll(t => (now - t).TotalMinutes > 30.0);
            if (_memoryRestarts.Count >= 3)
            {
                DegradeFps("3 memory restarts within 30 minutes");
            }

            await RestartPipelineAsync("FfmpegMemory", countsAsFailure: false).ConfigureAwait(false);
            return;
        }

        // Encoder speed
        var speed = _ffmpegHost.LastProgress.Speed;
        if (speed > 0 && speed < speedThreshold)
        {
            _lowSpeedSinceUtc ??= now;
            if (now - _lowSpeedSinceUtc.Value >= TimeSpan.FromSeconds(speedSeconds))
            {
                _lowSpeedSinceUtc = null;

                // Restarting only helps if the frame rate could actually be lowered.
                if (DegradeFps($"Encoding speed < {speedThreshold:F2} ({speed:F2}x) for {speedSeconds} seconds"))
                {
                    await RestartPipelineAsync("EncoderTooSlow", countsAsFailure: false).ConfigureAwait(false);
                    return;
                }
            }
        }
        else if (speed >= speedThreshold)
        {
            _lowSpeedSinceUtc = null;
        }

        if ((now - _lastWatchdogLogUtc).TotalSeconds >= 60.0)
        {
            _lastWatchdogLogUtc = now;
            Log.Information("Recording watchdog: WorkingSet={WsMb:F1} MB, Speed={Speed:F2}x, Fps={Fps:F1}",
                wsMb, speed, _ffmpegHost.LastProgress.Fps);
        }

        // AUD-01: Zero audio sources watchdog (>10s -> Degraded & toast). Expected when both are turned off.
        var expectsAudio = settings.Audio.MicMode != MicMode.None || settings.Audio.OutputMode != OutputMode.None;
        if (expectsAudio && _audioEngine.GetStatus().ActiveDevices.Count == 0)
        {
            _noAudioSinceUtc ??= now;
            if (now - _noAudioSinceUtc.Value >= NoAudioTimeout && !_noAudioToastShown)
            {
                _noAudioToastShown = true;
                Log.Warning("Watchdog: Recording has had no audio source for {Sec:F0}s.", (now - _noAudioSinceUtc.Value).TotalSeconds);
                NoAudioSourcesNotification?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _noAudioSinceUtc = null;
            _noAudioToastShown = false;
        }

        // REC-01: Disk write growth watchdog (no growth for 10s -> Faulted, critical toast, RestartPipeline)
        var currentBytes = _segmentSink.BytesWritten;
        if (currentBytes != _lastBytesWritten)
        {
            // Any change counts: a new part starts again from a few bytes.
            _lastBytesWritten = currentBytes;
            _lastBytesGrowthUtc = now;
        }
        else if (now - _lastBytesGrowthUtc >= DiskStallTimeout)
        {
            Log.Error("Watchdog: No disk write growth for {Sec:F0} seconds while recording (BytesWritten={Bytes}). Entering Faulted state.",
                (now - _lastBytesGrowthUtc).TotalSeconds, currentBytes);
            SetState(RecorderState.Faulted);
            DiskWriteStallNotification?.Invoke(this, EventArgs.Empty);
            await RestartPipelineAsync("NoDiskWriteGrowth", countsAsFailure: true).ConfigureAwait(false);
            return;
        }

        // REC-01: 30-second background comparison of on-disk length vs BytesWritten (±2 MB)
        if ((now - _lastDiskSizeCheckUtc).TotalSeconds >= 30.0)
        {
            _lastDiskSizeCheckUtc = now;
            var currentTsPath = _segmentSink.Current?.TsPath;
            if (!string.IsNullOrEmpty(currentTsPath))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (File.Exists(currentTsPath))
                        {
                            var diskLen = new FileInfo(currentTsPath).Length;
                            if (Math.Abs(diskLen - currentBytes) > 2 * 1024 * 1024)
                            {
                                Log.Warning("Watchdog: On-disk length ({DiskLen:N0} bytes) deviates from BytesWritten ({BytesWritten:N0} bytes) by >2 MB for {Path}",
                                    diskLen, currentBytes, currentTsPath);
                                _diskLengthMismatchWarning = true;
                            }
                            else
                            {
                                _diskLengthMismatchWarning = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Watchdog: Error checking file info for {Path}", currentTsPath);
                    }
                });
            }
        }
    }

    /// <summary>Lowers the frame rate one step. Returns false when it is already at the minimum.</summary>
    private bool DegradeFps(string reason)
    {
        var current = _settingsService.Current.Video.FrameRate;
        var newFps = current switch
        {
            > 24 => 24,
            > 15 => 15,
            > 10 => 10,
            _ => 5
        };

        if (newFps >= current)
        {
            Log.Warning("Cannot lower the frame rate below {Fps} fps. Reason for trying: {Reason}", current, reason);
            return false;
        }

        Log.Warning("Degrading frame rate from {Old} to {New} fps. Reason: {Reason}", current, newFps, reason);
        try
        {
            var updatedSettings = _settingsService.Current.Clone();
            updatedSettings.Video.FrameRate = newFps;
            _settingsService.Save(updatedSettings);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save the lowered frame rate.");
            return false;
        }

        AddSessionEvent("FpsDegraded", $"Frame rate lowered from {current} to {newFps} fps: {reason}");
        FpsDegradedNotification?.Invoke(this, newFps);
        return true;
    }

    private void OnSegmentClosed(object? sender, SegmentInfo seg)
    {
        var manifest = _activeManifest;
        if (manifest == null)
        {
            return;
        }

        try
        {
            var storage = _settingsService.Current.Storage;
            var format = storage.OutputFormat;
            var expLocation = Path.GetFullPath(Environment.ExpandEnvironmentVariables(seg.Location));
            var expTs = Path.GetFullPath(Environment.ExpandEnvironmentVariables(seg.TsPath));

            // The final file's extension must match the container the post-processor writes.
            var expFinal = format switch
            {
                OutputContainerFormat.Ts => expTs,
                OutputContainerFormat.Mp4 => Path.ChangeExtension(expTs, ".mp4"),
                _ => Path.ChangeExtension(expTs, ".mkv")
            };

            var endedUtc = seg.EndedAtUtc ?? _clock.UtcNow;
            var entry = new SegmentManifestEntry
            {
                Index = seg.Index,
                Location = expLocation,
                TsPath = Path.GetRelativePath(expLocation, expTs),
                FinalPath = Path.GetRelativePath(expLocation, expFinal),
                StartedAtUtc = seg.StartedAtUtc,
                EndedAtUtc = endedUtc,
                Bytes = seg.Bytes,
                OpenReason = seg.OpenReason.ToString(),
                CloseReason = seg.CloseReason?.ToString(),
                Remux = "Pending",
                // Approximation until the post-processor measures the real length.
                DurationSec = Math.Round(Math.Max(0.0, (endedUtc - seg.StartedAtUtc).TotalSeconds), 2)
            };

            List<MarkerEntry> markers;
            lock (manifest)
            {
                manifest.Segments.Add(entry);
                markers = manifest.Markers.ToList();
            }

            _manifestSavedInitial = true;
            SaveManifest(manifest);

            // A split inside a running recording starts a new part timer.
            if (seg.CloseReason is not null and not RotationReason.Manual && _sessionClock.IsRunning)
            {
                _sessionClock.StartPartSpan();
            }

            _postProcessor?.Enqueue(new RemuxJob
            {
                TsPath = expTs,
                FinalPath = expFinal,
                OutputFormat = format,
                KeepTsAfterRemux = storage.KeepTsAfterRemux,
                SessionTitle = $"ScreenVault {manifest.SessionId} part {seg.Index}",
                Markers = markers,
                SegmentStartUtc = seg.StartedAtUtc,
                SegmentEndUtc = endedUtc,
                SessionId = manifest.SessionId,
                SegmentIndex = seg.Index
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to record the closed segment {Path} in the session", seg.TsPath);
        }
    }

    private async Task StartPipelineAsync()
    {
        Log.Information("Starting recording pipeline for new session...");

        // A previous attempt that never recorded anything is simply dropped.
        DiscardSession();

        SetState(RecorderState.Starting);
        _sessionStartTimeUtc = _clock.UtcNow;
        _sessionStartLocal = _sessionStartTimeUtc.ToLocalTime();
        _sessionClock.Reset();
        _currentPartIndex = 1;
        _encoderFailures = 0;
        _memoryRestarts.Clear();

        try
        {
            var settings = _settingsService.Current;
            var locator = new FfmpegLocator();
            var paths = locator.Locate(settings.Advanced.FfmpegPath);

            _activeProfileName = ResolveProfileName(settings);
            var profile = EncoderProfile.Create(_activeProfileName, settings.Video.Quality, settings.Video.FrameRate);

            var sessionId = SegmentNaming.FormatSessionId(_sessionStartLocal);
            var manifest = new SessionManifest
            {
                SessionId = sessionId,
                Status = "Recording",
                StartedAtUtc = _sessionStartTimeUtc,
                AppVersion = typeof(RecordingController).Assembly.GetName().Version?.ToString(3) ?? "1.2.0",
                Video = new SessionVideoMeta
                {
                    EncoderProfile = _activeProfileName,
                    Fps = settings.Video.FrameRate,
                    Width = 1920,
                    Height = 1080
                }
            };

            var activeLoc = _storageManager?.GetStatus().ActiveLocationPath;
            if (string.IsNullOrEmpty(activeLoc))
            {
                var primary = settings.Storage.Locations.FirstOrDefault(l => l.Enabled)?.Path ?? @"%USERPROFILE%\Videos\Screen Recordings";
                activeLoc = Environment.ExpandEnvironmentVariables(primary);
            }

            _activeMetadataBackupDir = settings.Storage.MetadataNextToRecordings
                ? StorageDirectoryHelper.GetMetadataDirectory(new System.IO.Abstractions.FileSystem(), activeLoc)
                : null;

            _activeManifest = manifest;
            _manifestSavedInitial = false;
            _sessionStore?.RegisterLive(manifest, MetadataMirrors());
            _markerService?.SetActiveSession(manifest, _activeMetadataBackupDir, () => _sessionClock.Elapsed.TotalSeconds);

            var spec = FfmpegCommandBuilder.Build(paths.FfmpegPath, settings, profile, includeAudio: true);

            if (settings.Audio.UnmuteOnNewSession)
            {
                _audioEngine.SetMicMute(false);
            }

            await LaunchPipelineAsync(spec, settings).ConfigureAwait(false);
            Log.Information("Pipeline started successfully. Awaiting first bytes.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start recording pipeline.");
            await TearDownPipelineAsync().ConfigureAwait(false);
            _backoff.RecordFailure();
            _encoderFailures++;
            SetState(RecorderState.Faulted);
        }
    }

    private async Task ResumePipelineAsync()
    {
        Log.Information("Resuming recording pipeline for existing session {SessionId}...", _activeManifest?.SessionId);
        SetState(RecorderState.Starting);

        try
        {
            var settings = _settingsService.Current;
            var locator = new FfmpegLocator();
            var paths = locator.Locate(settings.Advanced.FfmpegPath);

            _activeProfileName = ResolveProfileName(settings);
            var profile = EncoderProfile.Create(_activeProfileName, settings.Video.Quality, settings.Video.FrameRate);

            _currentPartIndex = NextPartIndex();
            AddSessionEvent("Resumed", $"Resumed at part {_currentPartIndex}, elapsed offset: {_sessionClock.Elapsed:hh\\:mm\\:ss}", saveAlways: true);

            var spec = FfmpegCommandBuilder.Build(paths.FfmpegPath, settings, profile, includeAudio: true);
            await LaunchPipelineAsync(spec, settings).ConfigureAwait(false);

            Log.Information("Pipeline resumed successfully. Awaiting first bytes for part {Part}.", _currentPartIndex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to resume recording pipeline.");
            await TearDownPipelineAsync().ConfigureAwait(false);
            _backoff.RecordFailure();
            _encoderFailures++;
            SetState(_desired == DesiredState.Paused ? RecorderState.Paused : RecorderState.Faulted);
        }
    }

    private async Task LaunchPipelineAsync(FfmpegLaunchSpec spec, AppSettings settings)
    {
        var storage = settings.Storage;
        _segmentSink.ConfigureSplitting(
            storage.SplitMinutes > 0 ? TimeSpan.FromMinutes(storage.SplitMinutes) : null,
            storage.SplitSizeMb > 0 ? storage.SplitSizeMb * 1024L * 1024L : null);

        _segmentSink.BeginStream(_sessionStartLocal, _currentPartIndex);
        _audioEngine.EnsureCaptureRunning();

        var now = _clock.UtcNow;
        _pipelineStartedUtc = now;
        _lastBytesWritten = 0;
        _lastBytesGrowthUtc = now;
        _lowSpeedSinceUtc = null;
        _noAudioSinceUtc = null;
        _noAudioToastShown = false;
        _diskLengthMismatchWarning = false;

        await _ffmpegHost.StartAsync(spec, _segmentSink, CancellationToken.None).ConfigureAwait(false);
        _audioEngine.AttachOutput(_ffmpegHost.AudioInput);
    }

    /// <summary>Stops FFmpeg, the audio feed and the current part. Never throws.</summary>
    private async Task TearDownPipelineAsync()
    {
        try
        {
            _audioEngine.DetachOutput();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error detaching audio output.");
        }

        try
        {
            await _ffmpegHost.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping FFmpeg.");
        }

        try
        {
            _segmentSink.EndStream();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error closing the current part.");
        }
    }

    private async Task PausePipelineAsync()
    {
        Log.Information("Pausing recording pipeline...");
        SetState(RecorderState.Stopping);
        _sessionClock.StopSpan();

        await TearDownPipelineAsync().ConfigureAwait(false);

        SetState(RecorderState.Paused);
        _lastPauseReminderUtc = _clock.UtcNow;
        AddSessionEvent("Paused", $"Paused at elapsed offset: {_sessionClock.Elapsed:hh\\:mm\\:ss}", saveAlways: true);

        Log.Information("Recording pipeline paused cleanly.");
    }

    private async Task RestartPipelineAsync(string reason, bool countsAsFailure)
    {
        Log.Information("Restarting pipeline. Reason: {Reason}", reason);
        SetState(RecorderState.Recovering);
        _sessionClock.StopSpan();

        await TearDownPipelineAsync().ConfigureAwait(false);
        AddSessionEvent("PipelineRestarted", reason);

        if (countsAsFailure)
        {
            _backoff.RecordFailure();
            _encoderFailures++;

            if (_encoderFailures >= 3 && _activeProfileName != "x264")
            {
                Log.Warning("Repeated encoder failures detected ({Count}). Falling back to libx264 profile.", _encoderFailures);
                _activeProfileName = "x264";
            }

            if (_backoff.ConsecutiveFailures >= 3)
            {
                Log.Warning("Repeated pipeline failures ({Count}). Entering Faulted state for backoff.", _backoff.ConsecutiveFailures);
                SetState(RecorderState.Faulted);
                return;
            }
        }

        if (HasSessionData)
        {
            await ResumePipelineAsync().ConfigureAwait(false);
        }
        else
        {
            await StartPipelineAsync().ConfigureAwait(false);
        }
    }

    private async Task StopPipelineAsync()
    {
        Log.Information("Stopping and saving recording pipeline...");
        SetState(RecorderState.Saving);
        _sessionClock.StopSpan();

        await TearDownPipelineAsync().ConfigureAwait(false);

        var manifest = _activeManifest;
        if (manifest == null)
        {
            Log.Information("Recording pipeline stopped (no active session).");
            return;
        }

        long recordedBytes;
        int partCount;
        lock (manifest)
        {
            recordedBytes = manifest.Segments.Sum(s => s.Bytes);
            partCount = manifest.Segments.Count;
        }

        if (recordedBytes == 0 && _segmentSink.BytesWritten == 0)
        {
            Log.Warning("Session {SessionId} ended with 0 bytes (failed start). Deleting manifest if any.", manifest.SessionId);
            try
            {
                _sessionStore?.Delete(manifest.SessionId, MetadataMirrors());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not delete the empty session {SessionId}", manifest.SessionId);
            }

            ReleaseSession();
            return;
        }

        lock (manifest)
        {
            manifest.Status = "Completed";
            manifest.EndedAtUtc = _clock.UtcNow;
            manifest.Events.Add(new SessionEventEntry
            {
                AtUtc = _clock.UtcNow,
                Type = "SavedByUser",
                Detail = $"Recording stopped and saved with {partCount} part(s)"
            });
        }

        SaveManifest(manifest);
        ReleaseSession();

        try
        {
            SessionCompleted?.Invoke(this, manifest);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "A SessionCompleted handler failed.");
        }

        Log.Information("Recording pipeline stopped and saved.");
    }

    /// <summary>True once the active session has something on disk worth continuing.</summary>
    private bool HasSessionData
    {
        get
        {
            var manifest = _activeManifest;
            if (manifest == null)
            {
                return false;
            }

            if (_manifestSavedInitial)
            {
                return true;
            }

            lock (manifest)
            {
                return manifest.Segments.Count > 0;
            }
        }
    }

    private int NextPartIndex()
    {
        var manifest = _activeManifest;
        var highest = 0;
        if (manifest != null)
        {
            lock (manifest)
            {
                highest = manifest.Segments.Count > 0 ? manifest.Segments.Max(s => s.Index) : 0;
            }
        }

        return Math.Max(_currentPartIndex + 1, highest + 1);
    }

    private void AddSessionEvent(string type, string detail, bool saveAlways = false)
    {
        var manifest = _activeManifest;
        if (manifest == null)
        {
            return;
        }

        lock (manifest)
        {
            manifest.Events.Add(new SessionEventEntry
            {
                AtUtc = _clock.UtcNow,
                Type = type,
                Detail = detail
            });
        }

        if (_manifestSavedInitial || (saveAlways && HasSessionData))
        {
            SaveManifest(manifest);
        }
    }

    private void SaveManifest(SessionManifest manifest)
    {
        try
        {
            _sessionStore?.Save(manifest, MetadataMirrors());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save session manifest {SessionId}", manifest.SessionId);
        }
    }

    private string[]? MetadataMirrors() =>
        !string.IsNullOrEmpty(_activeMetadataBackupDir) ? [_activeMetadataBackupDir] : null;

    /// <summary>Drops an active session that never recorded anything.</summary>
    private void DiscardSession()
    {
        var manifest = _activeManifest;
        if (manifest == null)
        {
            return;
        }

        if (_manifestSavedInitial)
        {
            try
            {
                _sessionStore?.Delete(manifest.SessionId, MetadataMirrors());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not delete the abandoned session {SessionId}", manifest.SessionId);
            }
        }

        ReleaseSession();
    }

    private void ReleaseSession()
    {
        var manifest = _activeManifest;
        if (manifest != null)
        {
            _sessionStore?.UnregisterLive(manifest.SessionId);
        }

        _markerService?.ClearActiveSession();
        _activeManifest = null;
        _activeMetadataBackupDir = null;
        _manifestSavedInitial = false;
    }

    private string ResolveProfileName(AppSettings settings)
    {
        if (_encoderFailures >= 3)
        {
            return "x264";
        }

        if (!string.Equals(settings.Video.Encoder, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return settings.Video.Encoder;
        }

        if (!string.IsNullOrEmpty(settings.Video.DetectedEncoderProfile))
        {
            return settings.Video.DetectedEncoderProfile;
        }

        return "x264";
    }

    private void SetState(RecorderState newState)
    {
        if (_state != newState)
        {
            Log.Information("RecorderState transition: {Old} -> {New}", _state, newState);
            _state = newState;
            if (newState is RecorderState.Paused or RecorderState.Idle or RecorderState.Recovering or RecorderState.Faulted or RecorderState.Saving or RecorderState.Suspended)
            {
                _sessionClock.StopSpan();
            }
        }
    }

    private HealthSnapshot BuildHealthSnapshot()
    {
        var elapsed = _sessionClock.Elapsed;
        var partElapsed = _sessionClock.CurrentPartElapsed;

        var audioStatus = _audioEngine.GetStatus();
        var warnings = new List<string>(audioStatus.DegradedFlags);

        if (_backoff.ConsecutiveFailures > 0 && _desired != DesiredState.Stopped)
        {
            warnings.Add($"Recording had to restart {_backoff.ConsecutiveFailures} time(s) in a row");
        }

        if (_memoryHighWarning)
        {
            var wsMb = _ffmpegHost.WorkingSet64 / (1024.0 * 1024.0);
            warnings.Add($"FFmpeg memory high ({wsMb:F0} MB)");
        }

        if (_noAudioSinceUtc is { } noAudioSince && _clock.UtcNow - noAudioSince >= NoAudioTimeout)
        {
            warnings.Add("Recording has no audio source — check your devices");
        }

        if (_diskLengthMismatchWarning)
        {
            warnings.Add("Disk file size mismatch (>2 MB deviation)");
        }

        var isDegraded = warnings.Count > 0;

        // Only report a file while a session is active; after stopping, the last part's raw file
        // may already have been converted and removed.
        var hasSession = _activeManifest != null;
        var current = hasSession ? _segmentSink.Current : null;

        return new HealthSnapshot(
            _state,
            _desired,
            isDegraded,
            elapsed,
            current?.TsPath,
            _segmentSink.BytesWritten,
            audioStatus.ActiveDevices,
            warnings,
            _activeProfileName,
            _ffmpegHost.LastProgress.Fps,
            _ffmpegHost.LastProgress.Speed,
            partElapsed,
            current?.Index ?? _currentPartIndex,
            _ffmpegHost.WorkingSet64);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _reconcileTimer.DisposeAsync().ConfigureAwait(false);
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        if (_storageManager != null)
        {
            _storageManager.SwitchRequested -= OnStorageSwitchRequested;
        }

        _lock.Dispose();
    }
}
