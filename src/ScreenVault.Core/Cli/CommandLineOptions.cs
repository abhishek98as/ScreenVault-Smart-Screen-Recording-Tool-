namespace ScreenVault.Core.Cli;

public sealed class CommandLineOptions
{
    public bool StartRecording { get; set; }
    public bool StopRecording { get; set; }
    public bool Toggle { get; set; }
    public bool Pause { get; set; }
    public bool Resume { get; set; }
    public string? MarkerNote { get; set; }
    public bool AddMarker { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool ShowStatus { get; set; }
    public bool Status => ShowStatus;
    public bool OpenSettings { get; set; }
    public bool Settings => OpenSettings;
    public bool Exit { get; set; }
    public bool Portable { get; set; }
    public bool Recovered { get; set; }
    public bool Autostart { get; set; }
    public bool AfterUpgrade { get; set; }
    public bool Wait { get; set; }
    public string? LogLevel { get; set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (string.Equals(arg, "--log-level", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options.LogLevel = args[++i];
                }
            }
            else if (string.Equals(arg, "--startrecording", StringComparison.OrdinalIgnoreCase))
            {
                options.StartRecording = true;
            }
            else if (string.Equals(arg, "--stoprecording", StringComparison.OrdinalIgnoreCase))
            {
                options.StopRecording = true;
            }
            else if (string.Equals(arg, "--toggle", StringComparison.OrdinalIgnoreCase))
            {
                options.Toggle = true;
            }
            else if (string.Equals(arg, "--pause", StringComparison.OrdinalIgnoreCase))
            {
                options.Pause = true;
            }
            else if (string.Equals(arg, "--resume", StringComparison.OrdinalIgnoreCase))
            {
                options.Resume = true;
            }
            else if (string.Equals(arg, "--marker", StringComparison.OrdinalIgnoreCase))
            {
                options.AddMarker = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options.MarkerNote = args[++i];
                }
            }
            else if (string.Equals(arg, "--minimize-to-tray", StringComparison.OrdinalIgnoreCase))
            {
                options.MinimizeToTray = true;
            }
            else if (string.Equals(arg, "--status", StringComparison.OrdinalIgnoreCase))
            {
                options.ShowStatus = true;
            }
            else if (string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase))
            {
                options.OpenSettings = true;
            }
            else if (string.Equals(arg, "--exit", StringComparison.OrdinalIgnoreCase))
            {
                options.Exit = true;
            }
            else if (string.Equals(arg, "--portable", StringComparison.OrdinalIgnoreCase))
            {
                options.Portable = true;
            }
            else if (string.Equals(arg, "--recovered", StringComparison.OrdinalIgnoreCase))
            {
                options.Recovered = true;
            }
            else if (string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase))
            {
                options.Autostart = true;
            }
            else if (string.Equals(arg, "--after-upgrade", StringComparison.OrdinalIgnoreCase))
            {
                options.AfterUpgrade = true;
            }
            else if (string.Equals(arg, "--wait", StringComparison.OrdinalIgnoreCase))
            {
                options.Wait = true;
            }
        }

        return options;
    }
}
