using System.Globalization;
using ScreenVault.App.Ipc;
using ScreenVault.App.Platform;
using ScreenVault.App.UI;
using ScreenVault.App.UI.Controls;
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
    private readonly MidnightTimer _midnightTimer;
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
    private bool _micPrivacyChecked;
    private bool? _restartRegisteredForRecording;
    private volatile bool _isShuttingDown;
    private volatile bool _suppressSavedDialogForCliStop;
    private bool _markerPromptOpen;
    private volatile string? _recordingStartedForMeeting;
    private readonly SynchronizationContext _uiContext;

    public TrayApplicationContext(CommandLineOptions cliOptions, ISettingsService settingsService)
    {
        // Background services (recorder, meeting detector, IPC…) marshal UI work through this context.
        // No control exists yet at this point, so WinForms has not installed its context by itself.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }

        _uiContext = SynchronizationContext.Current!;
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
        _midnightTimer = new MidnightTimer(_segmentWriter);

        _ffmpegHost = new FfmpegHost();
        _audioEngine = new AudioEngine();
        _audioEngine.Start(_settingsService.Current.Audio);

        // Audio and storage settings apply immediately (new folders are used from the next part on).
        _settingsService.SettingsChanged += (_, s) =>
        {
            _audioEngine.ApplySettings(s.Audio);
            _storageManager.UpdateSettings(s.Storage);
        };

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
            Icon = TrayIconSet.GetIcon(RecorderState.Idle, isDegraded: false),
            Visible = true
        };

        _notificationPresenter = new NotificationPresenter(_notifyIcon, _settingsService);
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

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

        // 3. Hotkeys (decided on what the user asked for, so they also work while starting or retrying)
        _hotkeyService = new HotkeyService(_settingsService);
        _hotkeyService.StartStopPressed += async (_, _) => await ToggleRecordingAsync().ConfigureAwait(true);
        _hotkeyService.MuteMicPressed += (_, _) => _controller.ToggleMicMute();
        _hotkeyService.AddMarkerPressed += (_, _) => ShowMarkerPrompt();
        _hotkeyService.PauseResumePressed += async (_, _) => await TogglePauseAsync().ConfigureAwait(true);
        _hotkeyService.ShowStatusPressed += (_, _) => ToggleStatusForm();
        ReportHotkeyConflicts(_hotkeyService.RegisterHotkeys());

        // Shortcuts and appearance edited in Settings apply immediately (no restart needed).
        _settingsService.SettingsChanged += (_, s) => RunOnUi(() =>
        {
            ReportHotkeyConflicts(_hotkeyService.RegisterHotkeys());
            Theme.SetMode(s.General.Theme);
        });

        // 4. Controller Events & Watchdogs
        _controller.HealthChanged += OnHealthChanged;
        _controller.PauseReminderTriggered += (_, _) => _notificationPresenter.ShowPausedReminder();
        _controller.FpsDegradedNotification += (_, fps) => _notificationPresenter.ShowFpsDegraded(fps);
        _controller.NoAudioSourcesNotification += (_, _) => _notificationPresenter.ShowNoAudioSources();
        _controller.DiskWriteStallNotification += (_, _) => _notificationPresenter.ShowFaulted("Not saving to disk");
        _controller.AudioDeviceSwitchedNotification += (_, detail) =>
        {
            // "…Recording continues" only makes sense while recording.
            if (_controller.Desired != DesiredState.Stopped)
            {
                _notificationPresenter.ShowDeviceSwitched(detail);
            }
        };
        _controller.SessionCompleted += (_, manifest) => OnSessionCompleted(manifest);

        // 5. Meeting Detection
        _meetingDetector.MeetingStarted += (_, e) => OnMeetingStarted(e);
        _meetingDetector.MeetingEnded += (_, e) =>
        {
            if (_controller.State != RecorderState.Recording)
            {
                return;
            }

            _controller.AddMarker($"Meeting ended ({e.AppName})", "System");

            // Offer to stop only when this call is why we started recording.
            if (string.Equals(_recordingStartedForMeeting, e.AppKey, StringComparison.OrdinalIgnoreCase) &&
                _settingsService.Current.MeetingDetection.PromptStopWhenMeetingEnds)
            {
                _recordingStartedForMeeting = null;
                RunOnUi(() =>
                {
                    if (_controller.Desired == DesiredState.Stopped)
                    {
                        return;
                    }

                    var prompt = new MeetingEndedPromptForm(e.AppName, onStopRecording: () => { _ = _controller.StopAsync(); });
                    prompt.Show();
                });
            }
        };
        _meetingDetector.Start();

        // 6. Reminder Service
        _reminderService.ReminderTriggered += (_, _) =>
        {
            RunOnUi(() =>
            {
                if (_controller.Desired != DesiredState.Stopped)
                {
                    return;
                }

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

        // Show the status window when launched by the user; autostart honours "Start minimized".
        var showStatusWindow = !cliOptions.MinimizeToTray ||
                               (cliOptions.Autostart && !_settingsService.Current.General.MinimizeToTrayOnLaunch);
        if (showStatusWindow)
        {
            ShowStatusFlyout();
        }

        // 8. Background Startup Tasks
        _ = Task.Run(async () =>
        {
            try
            {
                await RunStartupTasksAsync(cliOptions, defaultsApplied, showStatusWindow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Startup tasks failed.");
                RunOnUi(() => _statusForm.SetStartupComplete(true));
            }
        });

        Log.Information("TrayApplicationContext initialized completely.");
    }

    private async Task RunStartupTasksAsync(CommandLineOptions cliOptions, bool defaultsApplied, bool statusWindowShown)
    {
        var fs = new System.IO.Abstractions.FileSystem();
        foreach (var loc in _settingsService.Current.Storage.Locations)
        {
            if (!loc.Enabled) continue;
            StorageDirectoryHelper.CleanupTempDirectory(fs, loc.Path, TimeSpan.FromHours(24));
            StorageDirectoryHelper.MigrateLegacyFolder(fs, loc.Path, _sessionStore);
        }

        // Background recovery
        var showWizard = _settingsService.IsFirstRun && !cliOptions.AfterUpgrade;
        var result = await _recoveryService.RunRecoveryAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(result.Message))
        {
            _notificationPresenter.ShowRecovered(result.Message);
        }
        else if (!statusWindowShown && !showWizard)
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

        // First launch for this user: walk through storage, audio and startup options once.
        if (showWizard)
        {
            RunOnUi(ShowFirstRunWizard);
        }

        // Autostart or resume recording
        if (cliOptions.AfterUpgrade)
        {
            var resumeState = ResumeStateService.TryGetValidResumeState(TimeSpan.FromMinutes(15));
            ResumeStateService.ClearResumeState();
            if (resumeState != null)
            {
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
        }
        else if (cliOptions.StartRecording || _settingsService.Current.General.StartRecordingOnLaunch)
        {
            var delay = _settingsService.Current.General.StartupDelaySeconds;
            if (delay > 0)
            {
                Log.Information("Waiting {Delay}s startup delay before starting recording...", delay);
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
            }

            if (_isShuttingDown)
            {
                return;
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
    }

    private void ShowFirstRunWizard()
    {
        try
        {
            using var wizard = new FirstRunWizardForm(_settingsService, _audioEngine);
            wizard.Shown += (_, _) => WindowActivator.BringToFront(wizard);
            wizard.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not show the setup wizard.");
        }
    }

    private void OnHealthChanged(object? sender, HealthSnapshot health)
    {
        if (health.State != _lastState || health.IsDegraded != _lastDegraded)
        {
            _lastState = health.State;
            _lastDegraded = health.IsDegraded;
            UpdateTrayIcon(health.State, health.IsDegraded);
        }

        // Windows hands desktop apps pure silence when microphone access is off in Privacy
        // settings: say so once per recording instead of saving hours without the user's voice.
        if (health.Desired == DesiredState.Stopped)
        {
            _micPrivacyChecked = false;
        }
        else if (health.State == RecorderState.Recording && !_micPrivacyChecked)
        {
            _micPrivacyChecked = true;
            if (_settingsService.Current.Audio.MicMode != MicMode.None && !MicPrivacyChecker.IsMicrophoneAccessAllowed())
            {
                Log.Warning("Windows privacy settings block desktop apps from the microphone; recordings will have no microphone audio.");
                _notificationPresenter.ShowInfo(
                    "Microphone blocked by Windows",
                    "Your voice isn't being recorded. Turn on \"Let desktop apps access your microphone\" in Windows Settings → Privacy & security → Microphone.");
            }
        }

        // If Windows restarts ScreenVault after a crash or an update, recording resumes only when it
        // was running.
        var recording = health.Desired != DesiredState.Stopped;
        if (_restartRegisteredForRecording != recording && !_isShuttingDown)
        {
            _restartRegisteredForRecording = recording;
            ApplicationRestart.Register(resumeRecording: recording);
        }
    }

    private void OnSessionCompleted(SessionManifest manifest)
    {
        _recordingStartedForMeeting = null;

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

        if (isCliStop || _isShuttingDown)
        {
            var lastSegment = manifest.Segments.LastOrDefault();
            var finalFile = manifest.MergedPath
                            ?? (lastSegment != null ? Path.Combine(lastSegment.Location, lastSegment.FinalPath) : string.Empty);
            if (!_isShuttingDown)
            {
                _notificationPresenter.ShowSaved(manifest.SessionId, finalFile);
            }
        }
        else if (_settingsService.Current.Saving.ShowSavedDialog)
        {
            RunOnUi(() =>
            {
                var dlg = new SavedDialog(manifest, _settingsService, _sessionStore, _playerLauncher, finalizeTask);
                dlg.Show();
            });
        }
        else
        {
            var lastSegment = manifest.Segments.LastOrDefault();
            var finalFile = lastSegment != null ? Path.Combine(lastSegment.Location, lastSegment.FinalPath) : string.Empty;
            _notificationPresenter.ShowSaved(manifest.SessionId, finalFile);
        }

        RunOnUi(() =>
        {
            if (_libraryForm is { IsDisposed: false, Visible: true })
            {
                _ = _libraryForm.LoadSessionsAsync();
            }
        });
    }

    private void OnMeetingStarted(MeetingDetectedEventArgs e)
    {
        var meetingSettings = _settingsService.Current.MeetingDetection;
        var isAutoStart = meetingSettings.Mode == MeetingDetectionMode.AutoStart ||
                          meetingSettings.AutoStartApps.Any(a => string.Equals(a, e.AppName, StringComparison.OrdinalIgnoreCase));
        var isRecording = _controller.Desired != DesiredState.Stopped;

        if (isRecording)
        {
            if (_controller.State == RecorderState.Recording)
            {
                _controller.AddMarker($"Meeting started ({e.AppName})", "System");
            }

            return;
        }

        if (meetingSettings.Mode == MeetingDetectionMode.Off)
        {
            return;
        }

        if (isAutoStart)
        {
            _recordingStartedForMeeting = e.AppKey;
            _ = _controller.StartAsync();
            _notificationPresenter.ShowMeetingStarted(e.AppName);
        }
        else if (meetingSettings.Mode == MeetingDetectionMode.Ask)
        {
            RunOnUi(() =>
            {
                if (_controller.Desired != DesiredState.Stopped)
                {
                    return;
                }

                var prompt = new MeetingPromptForm(
                    e.AppName,
                    onStartRecording: () =>
                    {
                        _recordingStartedForMeeting = e.AppKey;
                        _ = _controller.StartAsync();
                    },
                    onAlwaysForApp: AlwaysRecordMeetingsFrom);
                prompt.Show();
            });
        }
    }

    private void AlwaysRecordMeetingsFrom(string appName)
    {
        try
        {
            var updated = _settingsService.Current.Clone();
            if (!updated.MeetingDetection.AutoStartApps.Contains(appName, StringComparer.OrdinalIgnoreCase))
            {
                updated.MeetingDetection.AutoStartApps.Add(appName);
                _settingsService.Save(updated);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not remember to always record meetings from {App}", appName);
        }
    }

    private async Task ToggleRecordingAsync()
    {
        try
        {
            if (_controller.Desired == DesiredState.Stopped)
            {
                await _controller.StartAsync().ConfigureAwait(true);
            }
            else
            {
                await _controller.StopAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Start/stop shortcut failed.");
        }
    }

    private async Task TogglePauseAsync()
    {
        try
        {
            if (_controller.Desired == DesiredState.Recording)
            {
                await _controller.PauseAsync().ConfigureAwait(true);
            }
            else if (_controller.Desired == DesiredState.Paused)
            {
                await _controller.ResumeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Pause/resume shortcut failed.");
        }
    }

    private void ShowMarkerPrompt()
    {
        if (_markerPromptOpen)
        {
            return;
        }

        if (_controller.State is not (RecorderState.Recording or RecorderState.Paused))
        {
            _notificationPresenter.ShowInfo("Nothing is being recorded", "Start a recording first, then add markers to find moments later.");
            return;
        }

        _markerPromptOpen = true;
        try
        {
            var elapsed = _controller.Health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            using var dlg = new MarkerNoteForm(elapsed);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _markerService.AddMarker(dlg.NoteText, "User");
                _notificationPresenter.ShowMarkerAdded(DateTime.Now);
            }
        }
        finally
        {
            _markerPromptOpen = false;
        }
    }

    private void ReportHotkeyConflicts(IReadOnlyList<string> failed)
    {
        if (failed.Count == 0)
        {
            return;
        }

        var list = string.Join(", ", failed.Select(HotkeyField.DisplayText));
        _notificationPresenter.ShowInfo(
            "Some shortcuts are not available",
            $"{list} {(failed.Count == 1 ? "is" : "are")} already used by another app. Pick a different combination in Settings → Shortcuts.");
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        if (_notificationPresenter.LastShownKey == NotificationPresenter.PausedReminderKey &&
            _controller.Desired == DesiredState.Paused)
        {
            _ = _controller.ResumeAsync();
            return;
        }

        ShowStatusFlyout();
    }

    private void UpdateTrayIcon(RecorderState state, bool isDegraded)
    {
        RunOnUi(() =>
        {
            try
            {
                if (!_isShuttingDown)
                {
                    _notifyIcon.Icon = TrayIconSet.GetIcon(state, isDegraded);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to update tray icon.");
            }
        });
    }

    private void UpdateTooltip()
    {
        var health = _controller.Health;
        var storageStatus = _storageManager.GetStatus();

        // The active path is the expanded one; before the first recording use the primary location.
        var activeLoc = storageStatus.Locations.FirstOrDefault(l => string.Equals(l.ExpandedPath, storageStatus.ActiveLocationPath, StringComparison.OrdinalIgnoreCase))
                        ?? storageStatus.Locations.FirstOrDefault(l => l.Enabled);
        var freeGb = (activeLoc?.AvailableFreeBytes ?? 0) / (1024L * 1024L * 1024L);

        var stateStr = health.State switch
        {
            RecorderState.Recording => "● REC",
            RecorderState.Paused => "❚❚ PAUSED",
            RecorderState.Faulted => "! RETRYING",
            RecorderState.Starting or RecorderState.Recovering => "… STARTING",
            RecorderState.Saving or RecorderState.Stopping => "… SAVING",
            _ => "○ READY"
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
            ShowStatusFlyout();
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
                ShowStatusFlyout();
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

        OpenMainWindow(_libraryForm);
    }

    /// <summary>Opens the status flyout next to the tray, in front of whatever app is active.</summary>
    private void ShowStatusFlyout()
    {
        _statusForm.AnchorNearTray();
        WindowActivator.BringToFront(_statusForm);
    }

    private void ShowSettingsDialog()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settingsService, _audioEngine);
        }

        OpenMainWindow(_settingsForm);
    }

    /// <summary>
    /// Shows Settings or the Recordings library in front of every other window. Like a Windows
    /// flyout opening a full window, the status flyout steps aside unless it is pinned.
    /// </summary>
    private void OpenMainWindow(Form window)
    {
        if (_statusForm.Visible && !_statusForm.IsPinned)
        {
            _statusForm.Hide();
        }

        WindowActivator.BringToFront(window);
    }

    private async Task<IpcResponse> HandleIpcRequestAsync(IpcRequest request)
    {
        var health = _controller.Health;
        var elapsed = health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        IpcResponse Response(bool ok, string message) => new()
        {
            Ok = ok,
            State = _controller.State.ToString(),
            Elapsed = _controller.Health.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            Message = message
        };

        switch (request.Cmd.ToLowerInvariant())
        {
            case "start":
                await _controller.StartAsync().ConfigureAwait(false);
                return Response(_controller.Desired == DesiredState.Recording, _controller.State == RecorderState.Recording ? "Recording started" : $"Starting (state: {_controller.State})");

            case "stop":
                if (_controller.Desired == DesiredState.Stopped)
                {
                    return Response(true, "Not recording");
                }

                _suppressSavedDialogForCliStop = true;
                try
                {
                    await _controller.StopAsync().ConfigureAwait(false);
                }
                finally
                {
                    _suppressSavedDialogForCliStop = false;
                }

                return Response(true, "Recording stopped and saved");

            case "toggle":
                if (_controller.Desired != DesiredState.Stopped)
                {
                    _suppressSavedDialogForCliStop = true;
                    try
                    {
                        await _controller.StopAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        _suppressSavedDialogForCliStop = false;
                    }

                    return Response(true, "Recording stopped and saved");
                }

                await _controller.StartAsync().ConfigureAwait(false);
                return Response(true, "Recording started");

            case "pause":
                if (_controller.Desired == DesiredState.Stopped)
                {
                    return Response(false, "Not recording, nothing to pause");
                }

                await _controller.PauseAsync().ConfigureAwait(false);
                return Response(true, "Recording paused");

            case "resume":
                if (_controller.Desired != DesiredState.Paused)
                {
                    return Response(false, _controller.Desired == DesiredState.Recording ? "Already recording" : "Not paused");
                }

                await _controller.ResumeAsync().ConfigureAwait(false);
                return Response(true, "Recording resumed");

            case "marker":
                if (_controller.State is not (RecorderState.Recording or RecorderState.Paused))
                {
                    return Response(false, "Not recording, marker not added");
                }

                _markerService.AddMarker(request.Note ?? "CLI marker", "User");
                return Response(true, "Marker added");

            case "library":
                RunOnUi(ShowLibraryDialog);
                return Response(true, "Recordings library opened");

            case "mute-mic":
                _controller.SetMicMute(true);
                return Response(true, "Microphone muted in recording");

            case "unmute-mic":
                _controller.SetMicMute(false);
                return Response(true, "Microphone unmuted in recording");

            case "toggle-mic":
                _controller.ToggleMicMute();
                return Response(true, $"Microphone muted: {_controller.IsMicMuted}");

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
                return Response(true, "Settings opened");

            case "exit":
                RunOnUi(RequestExit);
                return new IpcResponse { Ok = true, State = "Exiting", Message = "ScreenVault exiting" };

            case "second_launch":
                _notificationPresenter.ShowSecondLaunch(health.Elapsed);
                RunOnUi(() =>
                {
                    ShowStatusFlyout();
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
        _statusForm.Hide();

        Task.Run(async () =>
        {
            try
            {
                await _controller.StopAsync().ConfigureAwait(false);
                await _controller.DisposeAsync().ConfigureAwait(false);
                await _pipeServer.DisposeAsync().ConfigureAwait(false);
                _meetingDetector.Dispose();
                _reminderService.Dispose();
                _midnightTimer.Dispose();
                _postProcessor.Dispose();
                _storageManager.Dispose();
                _retentionService.Dispose();
                _audioEngine.Dispose();
                _systemEventsMonitor.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during graceful shutdown.");
            }
            finally
            {
                // Windows and hotkeys belong to the UI thread: finish the shutdown there.
                RunOnUi(() =>
                {
                    _hotkeyService.Dispose();
                    ExitThread();
                });
            }
        });
    }

    private void RunOnUi(Action action)
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
