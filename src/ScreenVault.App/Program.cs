using System.Diagnostics;
using System.Text.Json;
using ScreenVault.App.Ipc;
using ScreenVault.App.Platform;
using ScreenVault.Core.Cli;
using ScreenVault.Core.Infrastructure;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var cli = CommandLineOptions.Parse(args);

        // 1. Initialize logging
        Logging.Initialize(cli.LogLevel ?? "Information", cli.Portable);
        Log.Information("ScreenVault launched with args: {Args}", string.Join(" ", args));

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("AppDomain.ProcessExit invoked. ExitCode={ExitCode}", Environment.ExitCode);
            Logging.CloseAndFlush();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "AppDomain.UnhandledException encountered (IsTerminating={Terminating}): {Obj}", e.IsTerminating, e.ExceptionObject);
            Logging.CloseAndFlush();
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Application.ThreadException encountered: {Message}", e.Exception.Message);
            Logging.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "TaskScheduler.UnobservedTaskException encountered.");
            e.SetObserved();
        };

        // 2. Autostart guard: if launched via --autostart but user disabled StartWithWindows, exit immediately
        if (cli.Autostart)
        {
            try
            {
                var settingsEarly = new SettingsService(isPortable: cli.Portable);
                if (!settingsEarly.Current.General.StartWithWindows)
                {
                    Log.Information("Launched with --autostart, but StartWithWindows is disabled for this user. Exiting immediately with code 0.");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to inspect settings during --autostart check. Proceeding.");
            }
        }

        // 3. Check single instance
        using var singleInstance = new SingleInstanceManager();
        var isPrimary = singleInstance.IsPrimaryInstance;

        if (!isPrimary)
        {
            Log.Information("Another instance is running. Forwarding command via IPC...");
            return ForwardCommandToPrimary(cli).GetAwaiter().GetResult();
        }

        // If primary instance was invoked with --exit only, nothing is running to stop
        if (cli.Exit)
        {
            Log.Information("Exit requested (--exit) but ScreenVault is not currently running.");
            return 2;
        }

        // If primary instance was invoked with --status only, nothing is running to query
        if (cli.Status)
        {
            if (ConsoleAttach.TryAttachToParentConsole())
            {
                Console.WriteLine("{\"state\":\"NotRunning\",\"message\":\"ScreenVault is not currently running\"}");
                ConsoleAttach.Detach();
            }
            return 2;
        }

        try
        {
            // 4. High DPI & visual styles
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 5. Register Windows Application Restart
            ApplicationRestart.Register();

            // 6. Load settings
            var settingsService = new SettingsService(isPortable: cli.Portable);
            Log.Information("Settings loaded. Video FrameRate={Fps}, SplitMinutes={Split}",
                settingsService.Current.Video.FrameRate,
                settingsService.Current.Storage.SplitMinutes);

            // 7. Run tray context
            Application.Run(new TrayApplicationContext(cli, settingsService));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "ScreenVault encountered an unhandled fatal error.");
            return 1;
        }
        finally
        {
            ApplicationRestart.Unregister();
            Log.Information("ScreenVault application exiting.");
            Logging.CloseAndFlush();
        }
    }

    private static async Task<int> ForwardCommandToPrimary(CommandLineOptions cli)
    {
        string cmd = "second_launch";
        string? note = null;

        if (cli.StartRecording) cmd = "start";
        else if (cli.StopRecording) cmd = "stop";
        else if (cli.Toggle) cmd = "toggle";
        else if (cli.Pause) cmd = "pause";
        else if (cli.Resume) cmd = "resume";
        else if (!string.IsNullOrEmpty(cli.MarkerNote))
        {
            cmd = "marker";
            note = cli.MarkerNote;
        }
        else if (cli.Status) cmd = "status";
        else if (cli.Settings) cmd = "settings";
        else if (cli.Exit) cmd = "exit";

        var response = await ControlPipeClient.SendCommandAsync(cmd, note).ConfigureAwait(false);

        if (cli.Status)
        {
            if (ConsoleAttach.TryAttachToParentConsole())
            {
                if (response != null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(response));
                }
                else
                {
                    Console.WriteLine("{\"state\":\"Unknown\",\"message\":\"Failed to communicate with running instance\"}");
                }
                ConsoleAttach.Detach();
            }
            return response?.Ok == true ? 0 : 1;
        }

        if (cli.Exit)
        {
            if (cli.Wait)
            {
                return WaitForInstanceExit(TimeSpan.FromSeconds(30));
            }
            return response?.Ok == true ? 0 : 1;
        }

        return response?.Ok == true ? 0 : 0;
    }

    private static int WaitForInstanceExit(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        Log.Information("Waiting for running ScreenVault instance to exit (timeout: {TimeoutSeconds}s)...", timeout.TotalSeconds);

        while (sw.Elapsed < timeout)
        {
            try
            {
                using var testMutex = Mutex.OpenExisting(AppConstants.AppMutexName);
                if (testMutex.WaitOne(TimeSpan.FromMilliseconds(250)))
                {
                    testMutex.ReleaseMutex();
                    Log.Information("Running ScreenVault instance has released mutex and exited successfully.");
                    return 0; // Exited cleanly
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Log.Information("ScreenVault mutex no longer exists; running instance has exited.");
                return 0; // Exited and handle closed
            }
            catch (AbandonedMutexException)
            {
                Log.Information("ScreenVault mutex was abandoned; running instance has terminated.");
                return 0; // Instance exited
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Transient exception while waiting for mutex release.");
            }

            Thread.Sleep(250);
        }

        Log.Warning("Timed out after {ElapsedSeconds}s waiting for ScreenVault instance to exit.", sw.Elapsed.TotalSeconds);
        return 1; // Timeout
    }
}