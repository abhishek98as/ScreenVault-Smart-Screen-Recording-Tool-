using System.Globalization;
using ScreenVault.App.Ipc;
using ScreenVault.App.Platform;
using ScreenVault.App.UI;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Cli;
using ScreenVault.Core.Ffmpeg;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.Output;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Recording;
using ScreenVault.Core.Sessions;
using ScreenVault.Core.Settings;
using ScreenVault.Core.Storage;
using ScreenVault.Core.SystemIntegration;
using Serilog;

namespace ScreenVault.App;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ISettingsService _settingsService;
    private readonly ISessionStore _sessionStore;
    private readonly MarkerService _markerService;
    private readonly StorageManager _storageManager;
    private readonly IFfprobeClient _ffprobeClient;
    private readonly IChapterWriter _chapterWriter;
    private readonly PostProcessor _postProcessor;
    private readonly SegmentWriter _segmentWriter;
    private readonly IFfmpegHost _ffmpegHost;
    private readonly AudioEngine _audioEngine;
    private readonly RecordingController _controller;
    private readonly RecoveryService _recoveryService;
    private readonly RetentionService _retentionService;
    private readonly SystemEventsMonitor _systemEventsMonitor;
    private readonly ControlPipeServer _pipeServer;
    private readonly HotkeyService _hotkeyService;
    private readonly NotificationPresenter _notificationPresenter;
    private readonly PlayerLauncher _playerLauncher;
    private readonly ClipExporter _clipExporter;
    private readonly MeetingDetector _meetingDetector;
    private readonly ReminderService _reminderService;

    private readonly NotifyIcon _notifyIcon;
    private readonly StatusForm _statusForm;
    private SettingsForm? _settingsForm;
    private LibraryForm? _libraryForm;
    private ContextMenuStrip? _trayMenu;
    private readonly RestartManagerWindow _restartManagerWindow;

    private readonly System.Windows.Forms.Timer _trayTimer;
    private RecorderState _lastState = RecorderState.Idle;
    private bool _lastDegraded;
    private bool _isShuttingDown;
    private bool _suppressSavedDialogForCliStop;
    private readonly SynchronizationContext? _uiContext;

    public TrayApplicationContext(CommandLineOptions cliOptions, ISettingsService settingsService)
    {
        _uiContext = SynchronizationContext.Current;
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        // 0. Check and Apply Installer Defaults (if any)
        bool defaultsApplied = false;
        try
        {
            defaultsApplied = InstallDefaultsService.TryApplyDefaults(_settingsService);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply installer defaults.");
        }

        // 1. Core Services Setup
        _sessionStore = new SessionStore();
        _markerService = new MarkerService(_sessionStore);
        _storageManager = new StorageManager(_settingsService.Current.Storage);
        _ffprobeClient = new FfprobeClient();
        _chapterWriter = new ChapterWriter();
        _postProcessor = new PostProcessor(_ffprobeClient, _chapterWriter, _sessionStore);

        _segmentWriter = new SegmentWriter(
            locationSelector: _storageManager.SelectLocationForNewSegment,
            onWriteFailure: _storageManager.ReportWriteFailure);

        _ffmpegHost = new FfmpegHost();
        _audioEngine = new AudioEngine();
        _audioEngine.Start(_settingsService.Current.Audio);
        _settingsService.SettingsChanged += (_, s) => _audioEngine.ApplySettings(s.Audio);

        _controller = new RecordingController(
            _settingsService,
            _ffmpegHost,
            _audioEngine,
            _segmentWriter,
            clock: SystemClock.Instance,
            sessionStore: _sessionStore,
            markerService: _markerService,
            postProcessor: _postProcessor,
            storageManager: _storageManager);

        _recoveryService = new RecoveryService(
            _sessionStore,
            _settingsService.Current.Storage,
            _postProcessor,
            ffprobeClient: _ffprobeClient);

        _retentionService = new RetentionService(
            _sessionStore,
            _settingsService);
        _retentionService.Start();

        _systemEventsMonitor = new SystemEventsMonitor(_controller);
        _playerLauncher = new PlayerLauncher(_settingsService);
        _clipExporter = new ClipExporter(_sessionStore);
        _meetingDetector = new MeetingDetector(_settingsService);
        _reminderService = new ReminderService(_settingsService, _controller);

        // 2. Tray & UI Setup
        _notifyIcon = new NotifyIcon
        {
            Text = "ScreenVault",
            Visible = true
        };
        UpdateTrayIcon(RecorderState.Idle, isDegraded: false);

        _notificationPresenter = new NotificationPresenter(_notifyIcon, _settingsService);

        _statusForm = new StatusForm(
            _controller,
            _audioEngine,
            _storageManager,
            _markerService,
            _settingsService,
            openSettingsAction: ShowSettingsDialog,
            playerLauncher: _playerLauncher,
            openLibraryAction: ShowLibraryDialog);

        _restartManagerWindow = new RestartManagerWindow(
            () => _controller.State == RecorderState.Recording,
            async () =>
            {
                await _controller.StopAsync().ConfigureAwait(false);
            });

        _notifyIcon.MouseClick += OnTrayIconMouseClick;

        // 3. Hotkeys
        _hotkeyService = new HotkeyService(_settingsService);
        _hotkeyService.StartStopPressed += async (_, _) =>
        {
            if (_controller.State is RecorderState.Recording or RecorderState.Paused)
            {
                await _controller.StopAsync().ConfigureAwait(true);
            }
            else if (_controller.State == RecorderState.Idle)
            {
                await _controller.StartAsync().ConfigureAwait(true);
            }
        };

        _hotkeyService.MuteMicPressed += (_, _) => _controller.ToggleMicMute();

        _hotkeyService.AddMarkerPressed += (_, _) =>
        {
            var elapsed = _controller.Health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            using var dlg = new MarkerNoteForm(elapsed);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _markerService.AddMarker(dlg.NoteText, "User");
                _notificationPresenter.ShowMarkerAdded(DateTime.Now);
            }
        };

        _hotkeyService.PauseResumePressed += async (_, _) =>
        {
            if (_controller.State == RecorderState.Recording)
            {
                await _controller.PauseAsync().ConfigureAwait(true);
            }
            else if (_controller.State == RecorderState.Paused)
            {
                await _controller.ResumeAsync().ConfigureAwait(true);
            }
        };

        _hotkeyService.ShowStatusPressed += (_, _) => ToggleStatusForm();
        _hotkeyService.RegisterHotkeys();

        // Shortcuts and appearance edited in Settings apply immediately (no restart needed).
        _settingsService.SettingsChanged += (_, s) => RunOnUi(() =>
        {
            _hotkeyService.RegisterHotkeys();
            Theme.SetMode(s.General.Theme);
        });

        // 4. Controller Events & Watchdogs
        _controller.HealthChanged += OnHealthChanged;
        _controller.PauseReminderTriggered += (_, _) => _notificationPresenter.ShowPausedReminder();
        _controller.FpsDegradedNotification += (_, fps) => _notificationPresenter.ShowFpsDegraded(fps);
        _controller.NoAudioSourcesNotification += (_, _) => _notificationPresenter.ShowNoAudioSources();
        _controller.DiskWriteStallNotification += (_, _) => _notificationPresenter.ShowFaulted("Not saving to disk");
        _controller.AudioDeviceSwitchedNotification += (_, detail) => _notificationPresenter.ShowDeviceSwitched(detail);
        _controller.SessionCompleted += (_, manifest) =>
        {
            var finalizeTask = Task.Run(async () =>
            {
                try
                {
                    return await _postProcessor.FinalizeSessionAsync(manifest.SessionId, _settingsService.Current.Saving).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to finalize session {SessionId}", manifest.SessionId);
                    return null;
                }
            });

            var isCliStop = _suppressSavedDialogForCliStop;
            _suppressSavedDialogForCliStop = false;

            if (isCliStop)
            {
                var finalFile = manifest.MergedPath ?? manifest.Segments.LastOrDefault()?.FinalPath ?? manifest.Segments.LastOrDefault()?.TsPath ?? string.Empty;
                _notificationPresenter.ShowSaved(manifest.SessionId, finalFile);
            }
            else if (_settingsService.Current.Saving.ShowSavedDialog)
            {
                RunOnUi(() =>
                {
                    var dlg = new SavedDialog(manifest, _settingsService, _sessionStore, _playerLauncher, finalizeTask);
                    dlg.Show();
                });
            }
        };

        // 5. Meeting Detection
        _meetingDetector.MeetingStarted += (_, e) =>
        {
            var meetingSettings = _settingsService.Current.MeetingDetection;
            var isAutoStart = meetingSettings.Mode == MeetingDetectionMode.AutoStart ||
                              meetingSettings.AutoStartApps.Any(a => string.Equals(a, e.AppName, StringComparison.OrdinalIgnoreCase));

            if (isAutoStart)
            {
                if (_controller.State == RecorderState.Idle)
                {
                    _ = _controller.StartAsync();
                    _notificationPresenter.ShowMeetingStarted(e.AppName);
                }
                else if (_controller.State == RecorderState.Recording)
                {
                    _controller.AddMarker($"Meeting started ({e.AppName})", "System");
                }
            }
            else if (meetingSettings.Mode == MeetingDetectionMode.Ask)
            {
                if (_controller.State == RecorderState.Idle)
                {
                    RunOnUi(() =>
                    {
                        var prompt = new MeetingPromptForm(
                            e.AppName,
                            onStartRecording: () => { _ = _controller.StartAsync(); },
                            onAlwaysForApp: app =>
                            {
                                _settingsService.Current.MeetingDetection.AutoStartApps.Add(app);
                                _settingsService.Save(_settingsService.Current);
                            });
                        prompt.Show();
                    });
                }
                else if (_controller.State == RecorderState.Recording)
                {
                    _controller.AddMarker($"Meeting started ({e.AppName})", "System");
                }
            }
        };

        _meetingDetector.MeetingEnded += (_, e) =>
        {
            if (_controller.State == RecorderState.Recording)
            {
                _controller.AddMarker($"Meeting ended ({e.AppName})", "System");
            }
        };
        _meetingDetector.Start();

        // 6. Reminder Service
        _reminderService.ReminderTriggered += (_, _) =>
        {
            RunOnUi(() =>
            {
                var prompt = new ReminderPromptForm(
                    onStartRecording: () => { _ = _controller.StartAsync(); },
                    _reminderService);
                prompt.Show();
            });
        };
        _reminderService.Start();

        // 7. IPC Server
        _pipeServer = new ControlPipeServer(HandleIpcRequestAsync);
        _pipeServer.Start();

        _trayTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _trayTimer.Tick += (_, _) => UpdateTooltip();
        _trayTimer.Start();

        // If launched manually without --minimize-to-tray, display Status window near tray
        if (!cliOptions.MinimizeToTray)
        {
            _statusForm.AnchorNearTray();
            _statusForm.Show();
            _statusForm.Activate();
        }

        // 6. Background Startup Tasks
        Task.Run(async () =>
        {
            var fs = new System.IO.Abstractions.FileSystem();
            foreach (var loc in _settingsService.Current.Storage.Locations)
            {
                if (!loc.Enabled) continue;
                StorageDirectoryHelper.CleanupTempDirectory(fs, loc.Path, TimeSpan.FromHours(24));
                StorageDirectoryHelper.MigrateLegacyFolder(fs, loc.Path, _sessionStore);
            }

            // Background recovery
            var result = await _recoveryService.RunRecoveryAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(result.Message))
            {
                _notificationPresenter.ShowRecovered(result.Message);
            }
            else
            {
                _notificationPresenter.ShowWelcome();
            }

            // If migrated from v1 to v2, show informational toast
            if (_settingsService.WasMigrated)
            {
                _notificationPresenter.ShowInfo("ScreenVault Settings Updated", "Video frame rate set to 15 fps for stability.");
            }

            // Mark startup complete on status form so Start button becomes enabled
            RunOnUi(() => _statusForm.SetStartupComplete(true));

            // Inform if installer defaults were newly applied
            if (defaultsApplied)
            {
                _notificationPresenter.ShowInfo("ScreenVault", "Settings from the installer were applied.");
            }

            // Autostart or resume recording
            if (cliOptions.AfterUpgrade)
            {
                var resumeState = ResumeStateService.TryGetValidResumeState(TimeSpan.FromMinutes(15));
                if (resumeState != null)
                {
                    ResumeStateService.ClearResumeState();
                    try
                    {
                        Log.Information("Resuming recording after upgrade...");
                        await _controller.StartAsync().ConfigureAwait(false);
                        _controller.AddMarker("Resumed after upgrade", "System");
                        var appVer = typeof(TrayApplicationContext).Assembly.GetName().Version?.ToString(3) ?? "1.2.0";
                        _notificationPresenter.ShowInfo("ScreenVault", $"ScreenVault updated to v{appVer} — recording resumed.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to resume recording after upgrade.");
                    }
                }
                else
                {
                    ResumeStateService.ClearResumeState();
                }
            }
            else if (cliOptions.StartRecording || _settingsService.Current.General.StartRecordingOnLaunch)
            {
                var delay = _settingsService.Current.General.StartupDelaySeconds;
                if (delay > 0)
                {
                    Log.Information("Waiting {Delay}s startup delay before starting recording...", delay);
                    await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                }

                try
                {
                    await _controller.StartAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to autostart recording.");
                }
            }
        });

        Log.Information("TrayApplicationContext initialized completely.");
    }

    private void OnHealthChanged(object? sender, HealthSnapshot health)
    {
        if (health.State != _lastState || health.IsDegraded != _lastDegraded)
        {
            _lastState = health.State;
            _lastDegraded = health.IsDegraded;
            UpdateTrayIcon(health.State, health.IsDegraded);
        }
    }

    private void UpdateTrayIcon(RecorderState state, bool isDegraded)
    {
        try
        {
            _notifyIcon.Icon = TrayIconSet.GetIcon(state, isDegraded);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to update tray icon.");
        }
    }

    private void UpdateTooltip()
    {
        var health = _controller.Health;
        var storageStatus = _storageManager.GetStatus();
        var activeLoc = storageStatus.Locations.FirstOrDefault(l => string.Equals(l.Path, storageStatus.ActiveLocationPath, StringComparison.OrdinalIgnoreCase));
        var freeGb = (activeLoc?.AvailableFreeBytes ?? 0) / (1024L * 1024L * 1024L);

        var stateStr = health.State switch
        {
            RecorderState.Recording => "● REC",
            RecorderState.Paused => "❚❚ PAUSED",
            RecorderState.Faulted => "! ERROR",
            _ => "○ IDLE"
        };

        var timeStr = health.Elapsed > TimeSpan.Zero
            ? health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : "00:00:00";

        var text = $"ScreenVault {stateStr} {timeStr} · {freeGb} GB free";
        if (text.Length > 63)
        {
            text = text[..63]; // NotifyIcon text max limit is 63 chars on standard Windows, 127 with newer APIs
        }

        try
        {
            _notifyIcon.Text = text;
        }
        catch
        {
            // Ignore text set error
        }
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ToggleStatusForm();
        }
        else if (e.Button == MouseButtons.Right)
        {
            ShowTrayMenu();
        }
    }

    private void ToggleStatusForm()
    {
        if (_statusForm.Visible)
        {
            _statusForm.Hide();
        }
        else
        {
            _statusForm.AnchorNearTray();
            _statusForm.Show();
            _statusForm.Activate();
        }
    }

    private void ShowTrayMenu()
    {
        // The menu is rebuilt on every right-click; dispose the previous one so handles don't leak.
        _trayMenu?.Dispose();
        var menu = TrayMenuBuilder.Build(
            _controller,
            _audioEngine,
            _storageManager,
            _sessionStore,
            _settingsService,
            _markerService,
            showStatusAction: () =>
            {
                _statusForm.AnchorNearTray();
                _statusForm.Show();
                _statusForm.Activate();
            },
            showSettingsAction: ShowSettingsDialog,
            showLibraryAction: ShowLibraryDialog,
            exitAction: RequestExit);

        // Show context menu near mouse cursor
        _trayMenu = menu;
        menu.Show(Cursor.Position);
    }

    private void ShowLibraryDialog()
    {
        if (_libraryForm == null || _libraryForm.IsDisposed)
        {
            _libraryForm = new LibraryForm(_sessionStore, _controller, _settingsService, _playerLauncher, _clipExporter);
        }

        if (!_libraryForm.Visible)
        {
            _libraryForm.Show();
        }
        _libraryForm.Activate();
    }

    private void ShowSettingsDialog()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settingsService, _audioEngine);
        }

        if (!_settingsForm.Visible)
        {
            _settingsForm.Show();
        }
        _settingsForm.Activate();
    }

    private async Task<IpcResponse> HandleIpcRequestAsync(IpcRequest request)
    {
        var health = _controller.Health;
        var elapsed = health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        switch (request.Cmd.ToLowerInvariant())
        {
            case "start":
                await _controller.StartAsync().ConfigureAwait(false);
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Elapsed = elapsed, Message = "Recording started" };

            case "stop":
                _suppressSavedDialogForCliStop = true;
                await _controller.StopAsync().ConfigureAwait(false);
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Elapsed = elapsed, Message = "Recording stopped" };

            case "toggle":
                if (_controller.State is RecorderState.Recording or RecorderState.Paused)
                {
                    _suppressSavedDialogForCliStop = true;
                    await _controller.StopAsync().ConfigureAwait(false);
                    return new IpcResponse { Ok = true, State = _controller.State.ToString(), Elapsed = elapsed, Message = "Recording stopped" };
                }
                else
                {
                    await _controller.StartAsync().ConfigureAwait(false);
                    return new IpcResponse { Ok = true, State = _controller.State.ToString(), Elapsed = elapsed, Message = "Recording started" };
                }

            case "pause":
                await _controller.PauseAsync().ConfigureAwait(false);
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Elapsed = elapsed, Message = "Recording paused" };

            case "resume":
                await _controller.ResumeAsync().ConfigureAwait(false);
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Elapsed = elapsed, Message = "Recording resumed" };

            case "marker":
                _markerService.AddMarker(request.Note ?? "CLI marker", "User");
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Elapsed = elapsed, Message = "Marker added" };

            case "library":
                RunOnUi(ShowLibraryDialog);
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Message = "Recordings library opened" };

            case "mute-mic":
                _controller.SetMicMute(true);
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Message = "Microphone muted in recording" };

            case "unmute-mic":
                _controller.SetMicMute(false);
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Message = "Microphone unmuted in recording" };

            case "toggle-mic":
                _controller.ToggleMicMute();
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Message = $"Microphone muted: {_controller.IsMicMuted}" };

            case "status":
                return new IpcResponse
                {
                    Ok = true,
                    State = _controller.State.ToString(),
                    Elapsed = elapsed,
                    File = health.CurrentFilePath,
                    Message = $"State={health.State}, Elapsed={elapsed}, Fps={health.ActualFps:F1}",
                    Warnings = health.DegradedWarnings.ToList()
                };

            case "settings":
                RunOnUi(ShowSettingsDialog);
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Message = "Settings opened" };

            case "exit":
                RunOnUi(RequestExit);
                return new IpcResponse { Ok = true, State = "Exiting", Message = "ScreenVault exiting" };

            case "second_launch":
                _notificationPresenter.ShowSecondLaunch(health.Elapsed);
                RunOnUi(() =>
                {
                    _statusForm.AnchorNearTray();
                    _statusForm.Show();
                    _statusForm.Activate();
                });
                return new IpcResponse { Ok = true, State = _controller.State.ToString(), Elapsed = elapsed };

            default:
                return new IpcResponse { Ok = false, Message = $"Unknown command: {request.Cmd}" };
        }
    }

    private void RequestExit()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        Log.Information("Shutting down ScreenVault application context. CallStack: {Stack}", Environment.StackTrace);

        if (_controller.State == RecorderState.Recording)
        {
            try
            {
                ResumeStateService.SaveResumeState(true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not save resume state during exit.");
            }
        }

        _trayTimer.Stop();
        _trayTimer.Dispose();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        _restartManagerWindow.Dispose();

        Task.Run(async () =>
        {
            try
            {
                await _controller.StopAsync().ConfigureAwait(false);
                await _controller.DisposeAsync().ConfigureAwait(false);
                await _pipeServer.DisposeAsync().ConfigureAwait(false);
                _meetingDetector.Dispose();
                _reminderService.Dispose();
                _postProcessor.Dispose();
                _storageManager.Dispose();
                _retentionService.Dispose();
                _audioEngine.Dispose();
                _hotkeyService.Dispose();
                _systemEventsMonitor.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during graceful shutdown.");
            }
            finally
            {
                ExitThread();
            }
        });
    }

    private void RunOnUi(Action action)
    {
        if (_uiContext != null)
        {
            _uiContext.Post(_ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in RunOnUi action.");
                }
            }, null);
        }
        else if (_statusForm.IsHandleCreated && !_statusForm.IsDisposed)
        {
            _statusForm.BeginInvoke(action);
        }
        else
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error running UI action directly.");
            }
        }
    }

    protected override void ExitThreadCore()
    {
        Log.Information("ExitThreadCore invoked. CallStack: {Stack}", Environment.StackTrace);
        _statusForm.Dispose();
        _trayMenu?.Dispose();
        _libraryForm?.Dispose();
        _settingsForm?.Dispose();
        base.ExitThreadCore();
    }
}
