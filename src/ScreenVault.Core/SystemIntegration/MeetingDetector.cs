using System.Runtime.Versioning;
using Microsoft.Win32;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.Core.SystemIntegration;

public sealed class MeetingDetectedEventArgs : EventArgs
{
    public string AppName { get; init; } = string.Empty;
    public string AppKey { get; init; } = string.Empty;
}

public interface IMeetingDetector : IDisposable
{
    void Start();
    void StopDetection();
    event EventHandler<MeetingDetectedEventArgs>? MeetingStarted;
    event EventHandler<MeetingDetectedEventArgs>? MeetingEnded;
}

[SupportedOSPlatform("windows")]
public sealed class MeetingDetector : IMeetingDetector
{
    private const string ConsentStoreMicPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private readonly ISettingsService _settingsService;
    private readonly System.Threading.Timer _pollTimer;
    private readonly object _lock = new();

    private readonly Dictionary<string, DateTime> _activeAppsFirstSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _triggeredApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _appLastStopped = new(StringComparer.OrdinalIgnoreCase);

    private bool _isRunning;
    private bool _isDisposed;

    public event EventHandler<MeetingDetectedEventArgs>? MeetingStarted;
    public event EventHandler<MeetingDetectedEventArgs>? MeetingEnded;

    /// <summary>True while at least one meeting app is actively using the microphone.</summary>
    public bool IsMeetingActive => _activeAppsFirstSeen.Count > 0;

    public MeetingDetector(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _pollTimer = new System.Threading.Timer(OnPollTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_isDisposed || _isRunning) return;
            _isRunning = true;
            _pollTimer.Change(1000, 3000); // Poll every 3 seconds
            Log.Information("MeetingDetector started.");
        }
    }

    public void StopDetection()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _activeAppsFirstSeen.Clear();
            _triggeredApps.Clear();
            Log.Information("MeetingDetector stopped.");
        }
    }

    private void OnPollTimerTick(object? state)
    {
        if (!_isRunning || _isDisposed) return;

        var meetingSettings = _settingsService.Current.MeetingDetection;
        if (meetingSettings.Mode == MeetingDetectionMode.Off) return;

        try
        {
            var currentInUse = QueryActiveMicrophoneApps(meetingSettings);
            var now = DateTime.UtcNow;

            List<MeetingDetectedEventArgs> newlyStarted = [];
            List<MeetingDetectedEventArgs> newlyEnded = [];

            lock (_lock)
            {
                // 1. Process active apps
                foreach (var (key, friendlyName) in currentInUse)
                {
                    // Respect 2-minute suppression after stop
                    if (_appLastStopped.TryGetValue(key, out var lastStopped) && (now - lastStopped) < TimeSpan.FromMinutes(2))
                    {
                        continue;
                    }

                    if (!_activeAppsFirstSeen.ContainsKey(key))
                    {
                        _activeAppsFirstSeen[key] = now;
                    }
                    else if (!_triggeredApps.Contains(key))
                    {
                        // Debounce: must be in use for >= 5 seconds
                        if ((now - _activeAppsFirstSeen[key]).TotalSeconds >= 5)
                        {
                            _triggeredApps.Add(key);
                            newlyStarted.Add(new MeetingDetectedEventArgs { AppKey = key, AppName = friendlyName });
                        }
                    }
                }

                // 2. Process ended apps
                var endedKeys = _triggeredApps.Where(k => !currentInUse.ContainsKey(k)).ToList();
                foreach (var key in endedKeys)
                {
                    _triggeredApps.Remove(key);
                    _activeAppsFirstSeen.Remove(key);
                    _appLastStopped[key] = now;

                    var friendly = ResolveFriendlyName(key);
                    newlyEnded.Add(new MeetingDetectedEventArgs { AppKey = key, AppName = friendly });
                }

                // Clean up seen apps that didn't trigger
                var untriggeredRemoved = _activeAppsFirstSeen.Keys.Where(k => !currentInUse.ContainsKey(k)).ToList();
                foreach (var key in untriggeredRemoved)
                {
                    _activeAppsFirstSeen.Remove(key);
                }
            }

            foreach (var evt in newlyStarted)
            {
                Log.Information("Meeting detected: {AppName} ({Key}) is using the microphone", evt.AppName, evt.AppKey);
                MeetingStarted?.Invoke(this, evt);
            }

            foreach (var evt in newlyEnded)
            {
                Log.Information("Meeting ended: {AppName} ({Key}) released the microphone", evt.AppName, evt.AppKey);
                MeetingEnded?.Invoke(this, evt);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error while checking microphone consent store registry.");
        }
    }

    private static Dictionary<string, string> QueryActiveMicrophoneApps(MeetingDetectionSettings settings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(ConsentStoreMicPath);
            if (baseKey == null) return result;

            // 1. Packaged apps
            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                if (string.Equals(subKeyName, "NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                if (subKeyName.Contains("ScreenVault", StringComparison.OrdinalIgnoreCase)) continue;

                if (IsAppMatching(subKeyName, settings))
                {
                    using var subKey = baseKey.OpenSubKey(subKeyName);
                    if (subKey != null && IsKeyActiveInUse(subKey))
                    {
                        result[subKeyName] = ResolveFriendlyName(subKeyName);
                    }
                }
            }

            // 2. Desktop (NonPackaged) apps
            using var nonPackagedKey = baseKey.OpenSubKey("NonPackaged");
            if (nonPackagedKey != null)
            {
                foreach (var subKeyName in nonPackagedKey.GetSubKeyNames())
                {
                    if (subKeyName.Contains("ScreenVault", StringComparison.OrdinalIgnoreCase)) continue;

                    if (IsAppMatching(subKeyName, settings))
                    {
                        using var subKey = nonPackagedKey.OpenSubKey(subKeyName);
                        if (subKey != null && IsKeyActiveInUse(subKey))
                        {
                            result[subKeyName] = ResolveFriendlyName(subKeyName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Exception querying registry microphone subkeys");
        }

        return result;
    }

    private static bool IsKeyActiveInUse(RegistryKey key)
    {
        var startObj = key.GetValue("LastUsedTimeStart");
        var stopObj = key.GetValue("LastUsedTimeStop");

        if (startObj is long start && stopObj is long stop)
        {
            return stop == 0 || start > stop;
        }

        return false;
    }

    private static bool IsAppMatching(string keyName, MeetingDetectionSettings settings)
    {
        if (settings.AnyApp) return true;

        foreach (var watched in settings.WatchedApps)
        {
            if (keyName.Contains(watched, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string ResolveFriendlyName(string keyName)
    {
        if (keyName.Contains("MSTeams", StringComparison.OrdinalIgnoreCase) ||
            keyName.Contains("Teams.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Teams";
        }

        if (keyName.Contains("Zoom.exe", StringComparison.OrdinalIgnoreCase)) return "Zoom";
        if (keyName.Contains("chrome.exe", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
        if (keyName.Contains("msedge.exe", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (keyName.Contains("firefox.exe", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (keyName.Contains("slack.exe", StringComparison.OrdinalIgnoreCase)) return "Slack";
        if (keyName.Contains("Discord.exe", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (keyName.Contains("webex", StringComparison.OrdinalIgnoreCase)) return "Webex";

        // Try extracting filename from NonPackaged hash path e.g. C:#Path#App.exe
        var parts = keyName.Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
        {
            var last = parts[^1];
            if (last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return last[..^4];
            }
            return last;
        }

        return keyName;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _isRunning = false;
            _pollTimer.Dispose();
        }
    }
}
