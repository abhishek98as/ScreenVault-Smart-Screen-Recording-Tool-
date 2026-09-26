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

    private RecorderState _state = RecorderState.Idle;
    private DesiredState _desired = DesiredState.Stopped;
    private DateTime _sessionStartTimeUtc;
    private DateTime _sessionStartLocal;
    private bool _displayChangePending;
    private DateTime _displayChangeRequestedUtc;
    private int _encoderFailures;
    private string _activeProfileName = "x264";
    private SessionManifest? _activeManifest;
    private string? _activeMetadataBackupDir;
    private bool _manifestSavedInitial;
    private int _currentPartIndex = 1;

    private bool _memoryHighWarning;
    private int _consecutiveLowSpeedSeconds;
    private DateTime _lastWatchdogLogUtc;
    private DateTime _lastPauseReminderUtc;
    private long _lastBytesWritten;
    private int _bytesWrittenStallSeconds;
    private DateTime _lastDiskSizeCheckUtc;
    private bool _diskLengthMismatchWarning;
    private int _zeroAudioSourcesSeconds;
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
            _storageManager.SwitchRequested += (_, args) =>
            {
                Log.Information("StorageManager requested switch to {Location}. Reason: {Reason}", args.NewLocation, args.Reason);
                _segmentSink.RequestRotation(RotationReason.StorageSwitch);
                _markerService?.AddEvent("StorageSwitched", args.Reason);
                _activeManifest?.Events.Add(new SessionEventEntry
                {
                    AtUtc = _clock.UtcNow,
                    Type = "StorageSwitched",
                    Detail = $"Switched to {args.NewLocation} ({args.Reason})"
                });
            };
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
            Log.Information("Resume requested by user.");
            _desired = DesiredState.Recording;
            await ReconcileLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void AddMarker(string? note, string kind = "User")
    {
        var elapsedSec = _sessionClock.Elapsed.TotalSeconds;
        Log.Information("Marker added: {Kind} - {Note} at {Elapsed}s", kind, note, elapsedSec);

        if (_markerService != null)
        {
            _markerService.AddMarker(note ?? string.Empty, kind);
        }
        else if (_activeManifest != null)
        {
            var offsetSec = Math.Max(0.0, (_clock.UtcNow - _activeManifest.StartedAtUtc).TotalSeconds);
            var entry = new MarkerEntry
            {
                AtUtc = _clock.UtcNow,
                OffsetSec = Math.Round(offsetSec, 2),
                Note = note ?? string.Empty,
                Kind = kind
            };
            _activeManifest.Markers.Add(entry);
            if (_manifestSavedInitial)
            {
                var mirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
                _sessionStore?.Save(_activeManifest, mirrors);
            }
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
            _activeManifest.Events.Add(new SessionEventEntry
            {
                AtUtc = _clock.UtcNow,
                Type = muted ? "MicMuted" : "MicUnmuted",
                Detail = label
            });
            if (_manifestSavedInitial)
            {
                var mirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
                _sessionStore?.Save(_activeManifest, mirrors);
            }
            _markerService?.AddMarker(label, "System");
        }
    }

    private void OnAudioDeviceSwitched(object? sender, AudioDeviceSwitchedEventArgs e)
    {
        Log.Information("Controller: Audio device switched - {Detail}", e.Detail);
        if (_activeManifest != null)
        {
            _activeManifest.Events.Add(new SessionEventEntry
            {
                AtUtc = _clock.UtcNow,
                Type = "AudioDeviceSwitched",
                Detail = e.Detail
            });
            if (_manifestSavedInitial)
            {
                var mirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
                _sessionStore?.Save(_activeManifest, mirrors);
            }
            _markerService?.AddEvent("AudioDeviceSwitched", e.Detail);
        }
        AudioDeviceSwitchedNotification?.Invoke(this, e.Detail);
    }

    private void TriggerReconcile()
    {
        if (_lock.CurrentCount == 0) return;

        Task.Run(async () =>
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
        });
    }

    private async Task ReconcileLockedAsync()
    {
        try
        {
            // 1. Reconcile state based on desired state
            switch (_desired, _state)
            {
                case (DesiredState.Recording, RecorderState.Idle):
                case (DesiredState.Recording, RecorderState.Faulted):
                    if (_backoff.IsBackoffElapsed())
                    {
                        await StartPipelineAsync().ConfigureAwait(false);
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
                    // Check if FFmpeg exited unexpectedly
                    if (!_ffmpegHost.IsRunning)
                    {
                        Log.Warning("Reconcile: FFmpeg stopped while DesiredState is Recording (State={State}). Restarting pipeline.", _state);
                        await RestartPipelineAsync("FfmpegExited").ConfigureAwait(false);
                        break;
                    }

                    // Check for hang (no stdout for > 5 seconds while running)
                    var silentSec = (_clock.UtcNow - _ffmpegHost.LastStdoutActivityUtc).TotalSeconds;
                    if (silentSec > 5.0 && _ffmpegHost.StdoutBytesTotal > 0)
                    {
                        Log.Warning("Reconcile: FFmpeg hang detected (no stdout bytes for {Sec:F1}s). Killing and restarting.", silentSec);
                        _ffmpegHost.Kill();
                        await RestartPipelineAsync("FfmpegHung").ConfigureAwait(false);
                        break;
                    }

                    // Check for pending debounced display change (2s debounce)
                    if (_displayChangePending && (DateTime.UtcNow - _displayChangeRequestedUtc).TotalSeconds >= 2.0)
                    {
                        _displayChangePending = false;
                        Log.Information("Reconcile: Applying debounced display change.");
                        await RestartPipelineAsync("DisplayChanged").ConfigureAwait(false);
                        break;
                    }

                    // Memory & Speed Watchdogs when recording
                    if (_state == RecorderState.Recording)
                    {
                        var wsBytes = _ffmpegHost.WorkingSet64;
                        var wsMb = wsBytes / (1024.0 * 1024.0);
                        _memoryHighWarning = wsMb > 400.0;

                        if (wsMb > 600.0)
                        {
                            Log.Warning("Watchdog: FFmpeg memory {WsMb:F1} MB exceeded 600 MB threshold. Triggering restart.", wsMb);
                            _activeManifest?.Events.Add(new SessionEventEntry
                            {
                                AtUtc = _clock.UtcNow,
                                Type = "FfmpegMemoryRestart",
                                Detail = $"Working set reached {wsMb:F1} MB"
                            });

                            _memoryRestarts.Add(_clock.UtcNow);
                            _memoryRestarts.RemoveAll(t => (_clock.UtcNow - t).TotalMinutes > 30.0);
                            if (_memoryRestarts.Count >= 3)
                            {
                                DegradeFps("3 memory restarts within 30 minutes");
                            }

                            await RestartPipelineAsync("FfmpegMemory").ConfigureAwait(false);
                            break;
                        }

                        var speed = _ffmpegHost.LastProgress.Speed;
                        if (speed > 0 && speed < 0.95)
                        {
                            _consecutiveLowSpeedSeconds++;
                            if (_consecutiveLowSpeedSeconds >= 20)
                            {
                                _consecutiveLowSpeedSeconds = 0;
                                DegradeFps($"Encoding speed < 0.95 ({speed:F2}x) for 20 seconds");
                                await RestartPipelineAsync("EncoderTooSlow").ConfigureAwait(false);
                                break;
                            }
                        }
                        else if (speed >= 0.95)
                        {
                            _consecutiveLowSpeedSeconds = 0;
                        }

                        if ((_clock.UtcNow - _lastWatchdogLogUtc).TotalSeconds >= 60.0)
                        {
                            _lastWatchdogLogUtc = _clock.UtcNow;
                            Log.Information("Recording watchdog: WorkingSet={WsMb:F1} MB, Speed={Speed:F2}x, Fps={Fps:F1}",
                                wsMb, speed, _ffmpegHost.LastProgress.Fps);
                        }

                        // AUD-01: Zero audio sources watchdog (>10s -> Degraded & toast)
                        var audioStatusCheck = _audioEngine.GetStatus();
                        if (audioStatusCheck.ActiveDevices.Count == 0)
                        {
                            _zeroAudioSourcesSeconds++;
                            if (_zeroAudioSourcesSeconds >= 10 && !_noAudioToastShown)
                            {
                                _noAudioToastShown = true;
                                Log.Warning("Watchdog: Recording has zero audio sources for {Sec}s.", _zeroAudioSourcesSeconds);
                                NoAudioSourcesNotification?.Invoke(this, EventArgs.Empty);
                            }
                        }
                        else
                        {
                            _zeroAudioSourcesSeconds = 0;
                            _noAudioToastShown = false;
                        }

                        // REC-01: Disk write growth watchdog (no growth for 10s -> Faulted, critical toast, RestartPipeline)
                        var currentBytes = _segmentSink.BytesWritten;
                        if (currentBytes > _lastBytesWritten)
                        {
                            _lastBytesWritten = currentBytes;
                            _bytesWrittenStallSeconds = 0;

                            if (!_manifestSavedInitial && _activeManifest != null && currentBytes > 0)
                            {
                                _manifestSavedInitial = true;
                                var mirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
                                _sessionStore?.Save(_activeManifest, mirrors);
                                Log.Information("Initial session manifest saved after first bytes received on disk for {SessionId}", _activeManifest.SessionId);
                            }
                        }
                        else
                        {
                            _bytesWrittenStallSeconds++;
                            if (_bytesWrittenStallSeconds >= 10)
                            {
                                _bytesWrittenStallSeconds = 0;
                                Log.Error("Watchdog: No disk write growth for 10 seconds while recording (BytesWritten={Bytes}). Entering Faulted state.", currentBytes);
                                SetState(RecorderState.Faulted);
                                DiskWriteStallNotification?.Invoke(this, EventArgs.Empty);
                                await RestartPipelineAsync("NoDiskWriteGrowth").ConfigureAwait(false);
                                break;
                            }
                        }

                        // REC-01: 30-second background comparison of on-disk length vs BytesWritten (±2 MB)
                        if ((_clock.UtcNow - _lastDiskSizeCheckUtc).TotalSeconds >= 30.0)
                        {
                            _lastDiskSizeCheckUtc = _clock.UtcNow;
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
                    break;

                case (DesiredState.Paused, RecorderState.Recording):
                case (DesiredState.Paused, RecorderState.Starting):
                    await PausePipelineAsync().ConfigureAwait(false);
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
            if (_state == RecorderState.Starting && _ffmpegHost.StdoutBytesTotal > 0)
            {
                SetState(RecorderState.Recording);
                _sessionClock.StartSpan();
                _backoff.RecordSuccess();
            }

            // FILE-02: Create manifest once the first segment has bytes on disk
            if (!_manifestSavedInitial && _segmentSink.BytesWritten > 0 && _activeManifest != null)
            {
                _manifestSavedInitial = true;
                var mirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
                _sessionStore?.Save(_activeManifest, mirrors);
                Log.Information("Initial session manifest saved to disk for session {SessionId}", _activeManifest.SessionId);
            }

            HealthChanged?.Invoke(this, BuildHealthSnapshot());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in RecordingController ReconcileLockedAsync.");
            _backoff.RecordFailure();
            if (_backoff.ConsecutiveFailures >= 3)
            {
                SetState(RecorderState.Faulted);
            }
        }
    }

    private void DegradeFps(string reason)
    {
        var current = _settingsService.Current.Video.FrameRate;
        var newFps = current switch
        {
            > 24 => 24,
            > 15 => 15,
            > 10 => 10,
            _ => 5
        };

        if (newFps < current)
        {
            Log.Warning("Degrading frame rate from {Old} to {New} fps. Reason: {Reason}", current, newFps, reason);
            var updatedSettings = _settingsService.Current.Clone();
            updatedSettings.Video.FrameRate = newFps;
            _settingsService.Save(updatedSettings);

            _activeManifest?.Events.Add(new SessionEventEntry
            {
                AtUtc = _clock.UtcNow,
                Type = "FpsDegraded",
                Detail = $"Frame rate lowered from {current} to {newFps} fps: {reason}"
            });

            FpsDegradedNotification?.Invoke(this, newFps);
        }
    }

    private void OnSegmentClosed(object? sender, SegmentInfo seg)
    {
        if (_activeManifest != null)
        {
            var expLocation = Path.GetFullPath(Environment.ExpandEnvironmentVariables(seg.Location));
            var expTs = Path.GetFullPath(Environment.ExpandEnvironmentVariables(seg.TsPath));
            var expFinal = Path.GetFullPath(Environment.ExpandEnvironmentVariables(seg.FinalPath));

            var relTs = Path.GetRelativePath(expLocation, expTs);
            var relFinal = Path.GetRelativePath(expLocation, expFinal);

            var entry = new SegmentManifestEntry
            {
                Index = seg.Index,
                Location = expLocation,
                TsPath = relTs,
                FinalPath = relFinal,
                StartedAtUtc = seg.StartedAtUtc,
                EndedAtUtc = seg.EndedAtUtc,
                Bytes = seg.Bytes,
                OpenReason = seg.OpenReason.ToString(),
                CloseReason = seg.CloseReason?.ToString(),
                Remux = "Pending"
            };
            _activeManifest.Segments.Add(entry);

            _manifestSavedInitial = true;
            var mirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
            _sessionStore?.Save(_activeManifest, mirrors);

            if (_postProcessor != null)
            {
                var job = new RemuxJob
                {
                    TsPath = seg.TsPath,
                    FinalPath = seg.FinalPath,
                    OutputFormat = _settingsService.Current.Storage.OutputFormat,
                    KeepTsAfterRemux = _settingsService.Current.Storage.KeepTsAfterRemux,
                    SessionTitle = $"ScreenVault {_activeManifest.SessionId} part {seg.Index}",
                    Markers = _activeManifest.Markers.ToList(),
                    SegmentStartUtc = seg.StartedAtUtc,
                    SegmentEndUtc = seg.EndedAtUtc ?? _clock.UtcNow,
                    SessionId = _activeManifest.SessionId,
                    SegmentIndex = seg.Index
                };
                _postProcessor.Enqueue(job);
            }
        }

        _lastBytesWritten = 0;
        _bytesWrittenStallSeconds = 0;
    }

    private async Task StartPipelineAsync()
    {
        Log.Information("Starting recording pipeline for new session...");
        SetState(RecorderState.Starting);
        _sessionStartTimeUtc = _clock.UtcNow;
        _sessionStartLocal = _clock.UtcNow.ToLocalTime();
        _sessionClock.Reset();
        _currentPartIndex = 1;

        try
        {
            var settings = _settingsService.Current;
            var locator = new FfmpegLocator();
            var paths = locator.Locate(settings.Advanced.FfmpegPath);

            _activeProfileName = ResolveProfileName(settings);
            var profile = EncoderProfile.Create(_activeProfileName, settings.Video.Quality, settings.Video.FrameRate);

            var sessionId = SegmentNaming.FormatSessionId(_sessionStartLocal);
            _activeManifest = new SessionManifest
            {
                SessionId = sessionId,
                Status = "Recording",
                StartedAtUtc = _sessionStartTimeUtc,
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

            _markerService?.SetActiveSession(_activeManifest, _activeMetadataBackupDir);
            _manifestSavedInitial = false;

            var spec = FfmpegCommandBuilder.Build(paths.FfmpegPath, settings, profile, includeAudio: true);

            _segmentSink.BeginStream(_sessionStartLocal, _currentPartIndex);
            if (settings.Audio.UnmuteOnNewSession)
            {
                _audioEngine.SetMicMute(false);
            }
            _audioEngine.EnsureCaptureRunning();
            _lastBytesWritten = 0;
            _bytesWrittenStallSeconds = 0;
            _zeroAudioSourcesSeconds = 0;
            _noAudioToastShown = false;
            _diskLengthMismatchWarning = false;

            await _ffmpegHost.StartAsync(spec, _segmentSink, CancellationToken.None).ConfigureAwait(false);
            _audioEngine.AttachOutput(_ffmpegHost.AudioInput);

            Log.Information("Pipeline started successfully. Awaiting first bytes.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start recording pipeline.");
            _backoff.RecordFailure();
            _encoderFailures++;
            SetState(_backoff.ConsecutiveFailures >= 3 ? RecorderState.Faulted : RecorderState.Idle);
            throw;
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

            _currentPartIndex = Math.Max(_currentPartIndex + 1, (_activeManifest?.Segments.Count ?? 0) + 1);

            if (_activeManifest != null)
            {
                _activeManifest.Events.Add(new SessionEventEntry
                {
                    AtUtc = _clock.UtcNow,
                    Type = "Resumed",
                    Detail = $"Resumed at part {_currentPartIndex}, elapsed offset: {_sessionClock.Elapsed:hh\\:mm\\:ss}"
                });
                var mirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
                _sessionStore?.Save(_activeManifest, mirrors);
            }

            var spec = FfmpegCommandBuilder.Build(paths.FfmpegPath, settings, profile, includeAudio: true);

            _segmentSink.BeginStream(_sessionStartLocal, _currentPartIndex);
            _audioEngine.EnsureCaptureRunning();
            _lastBytesWritten = 0;
            _bytesWrittenStallSeconds = 0;
            _zeroAudioSourcesSeconds = 0;
            _noAudioToastShown = false;
            _diskLengthMismatchWarning = false;

            await _ffmpegHost.StartAsync(spec, _segmentSink, CancellationToken.None).ConfigureAwait(false);
            _audioEngine.AttachOutput(_ffmpegHost.AudioInput);

            Log.Information("Pipeline resumed successfully. Awaiting first bytes for part {Part}.", _currentPartIndex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to resume recording pipeline.");
            _backoff.RecordFailure();
            _encoderFailures++;
            SetState(_backoff.ConsecutiveFailures >= 3 ? RecorderState.Faulted : RecorderState.Paused);
            throw;
        }
    }

    private async Task PausePipelineAsync()
    {
        Log.Information("Pausing recording pipeline...");
        SetState(RecorderState.Stopping);
        _sessionClock.StopSpan();

        _audioEngine.DetachOutput();
        await _ffmpegHost.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        _segmentSink.EndStream();

        SetState(RecorderState.Paused);
        _lastPauseReminderUtc = _clock.UtcNow;

        if (_activeManifest != null)
        {
            _activeManifest.Events.Add(new SessionEventEntry
            {
                AtUtc = _clock.UtcNow,
                Type = "Paused",
                Detail = $"Paused at elapsed offset: {_sessionClock.Elapsed:hh\\:mm\\:ss}"
            });
            var mirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
            _sessionStore?.Save(_activeManifest, mirrors);
        }

        Log.Information("Recording pipeline paused cleanly.");
    }

    private async Task RestartPipelineAsync(string reason)
    {
        Log.Information("Restarting pipeline. Reason: {Reason}", reason);
        SetState(RecorderState.Recovering);
        _sessionClock.StopSpan();

        _audioEngine.DetachOutput();
        await _ffmpegHost.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        _segmentSink.EndStream();

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

        try
        {
            if (_activeManifest != null)
            {
                await ResumePipelineAsync().ConfigureAwait(false);
            }
            else
            {
                await StartPipelineAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restart pipeline.");
        }
    }

    private async Task StopPipelineAsync()
    {
        Log.Information("Stopping and saving recording pipeline...");
        SetState(RecorderState.Saving);
        _sessionClock.StopSpan();

        _audioEngine.DetachOutput();
        await _ffmpegHost.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        _segmentSink.EndStream();

        if (_activeManifest != null)
        {
            var totalBytes = _activeManifest.Segments.Sum(s => s.Bytes);
            if (totalBytes == 0 && _segmentSink.BytesWritten == 0)
            {
                Log.Warning("Session {SessionId} ended with 0 bytes (failed start). Deleting manifest if any.", _activeManifest.SessionId);
                var mirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
                _sessionStore?.Delete(_activeManifest.SessionId, mirrors);
                _markerService?.ClearActiveSession();
                _activeManifest = null;
                _activeMetadataBackupDir = null;
                _manifestSavedInitial = false;
                SetState(RecorderState.Idle);
                return;
            }

            _activeManifest.Status = "Completed";
            _activeManifest.EndedAtUtc = _clock.UtcNow;
            _activeManifest.Events.Add(new SessionEventEntry
            {
                AtUtc = _clock.UtcNow,
                Type = "SavedByUser",
                Detail = $"Recording stopped and saved with {_activeManifest.Segments.Count} part(s)"
            });

            var stopMirrors = !string.IsNullOrEmpty(_activeMetadataBackupDir) ? new[] { _activeMetadataBackupDir } : null;
            _sessionStore?.Save(_activeManifest, stopMirrors);
            _markerService?.ClearActiveSession();

            var completedSession = _activeManifest;
            _activeManifest = null;
            _activeMetadataBackupDir = null;
            _manifestSavedInitial = false;

            SessionCompleted?.Invoke(this, completedSession);
        }

        Log.Information("Recording pipeline stopped and saved.");
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

        if (_backoff.ConsecutiveFailures > 0)
        {
            warnings.Add($"Pipeline failure backoff active (Failures: {_backoff.ConsecutiveFailures})");
        }

        if (_memoryHighWarning)
        {
            var wsMb = _ffmpegHost.WorkingSet64 / (1024.0 * 1024.0);
            warnings.Add($"FFmpeg memory high ({wsMb:F0} MB)");
        }

        if (_zeroAudioSourcesSeconds >= 10)
        {
            warnings.Add("Recording has no audio source — check your devices");
        }

        if (_diskLengthMismatchWarning)
        {
            warnings.Add("Disk file size mismatch (>2 MB deviation)");
        }

        var isDegraded = warnings.Count > 0;

        return new HealthSnapshot(
            _state,
            _desired,
            isDegraded,
            elapsed,
            _segmentSink.Current?.TsPath,
            _segmentSink.BytesWritten,
            audioStatus.ActiveDevices,
            warnings,
            _activeProfileName,
            _ffmpegHost.LastProgress.Fps,
            _ffmpegHost.LastProgress.Speed,
            partElapsed,
            _currentPartIndex,
            _ffmpegHost.WorkingSet64);
    }

    public async ValueTask DisposeAsync()
    {
        await _reconcileTimer.DisposeAsync().ConfigureAwait(false);
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lock.Dispose();
    }
}
