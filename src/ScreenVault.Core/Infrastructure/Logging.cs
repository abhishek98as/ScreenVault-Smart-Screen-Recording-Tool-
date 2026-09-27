using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ScreenVault.Core.Infrastructure;

public static class Logging
{
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    public static string GetLogsDirectory(bool isPortable = false)
    {
        if (isPortable)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ScreenVault", "logs");
    }

    public static void Initialize(string logLevelString = "Information", bool isPortable = false)
    {
        var logDir = GetLogsDirectory(isPortable);
        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // e.g. a portable copy in a read-only folder: log to the per-user folder instead of failing to start.
            logDir = GetLogsDirectory(isPortable: false);
            Directory.CreateDirectory(logDir);
        }

        LevelSwitch.MinimumLevel = ParseLevel(logLevelString);

        var logPath = Path.Combine(logDir, "sv-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logPath,
                shared: true,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 14,
                rollOnFileSizeLimit: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("ScreenVault logging initialized. Log directory: {LogDir}", logDir);
    }

    /// <summary>Changes how much is logged from now on (the "Log detail" setting).</summary>
    public static void SetLevel(string? logLevelString)
    {
        var level = ParseLevel(logLevelString);
        if (LevelSwitch.MinimumLevel != level)
        {
            LevelSwitch.MinimumLevel = level;
            Log.Information("Log level set to {Level}", level);
        }
    }

    private static LogEventLevel ParseLevel(string? logLevelString) =>
        Enum.TryParse<LogEventLevel>(logLevelString, true, out var level) ? level : LogEventLevel.Information;

    public static void CloseAndFlush()
    {
        Log.CloseAndFlush();
    }
}
