using Serilog;
using Serilog.Events;

namespace ScreenVault.Core.Infrastructure;

public static class Logging
{
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
        Directory.CreateDirectory(logDir);

        if (!Enum.TryParse<LogEventLevel>(logLevelString, true, out var level))
        {
            level = LogEventLevel.Information;
        }

        var logPath = Path.Combine(logDir, "sv-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
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

    public static void CloseAndFlush()
    {
        Log.CloseAndFlush();
    }
}
