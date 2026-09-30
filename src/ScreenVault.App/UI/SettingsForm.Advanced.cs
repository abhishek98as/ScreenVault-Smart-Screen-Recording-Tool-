using System.Diagnostics;
using System.IO.Compression;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private readonly TextField _txtFfmpegPath = new() { PlaceholderText = "Bundled FFmpeg (recommended)" };
    private readonly ModernComboBox _cmbLogLevel = new();
    private readonly NumberField _numFfmpegWarnMb = new() { Minimum = 100, Maximum = 4000, Value = 400, Suffix = "MB" };
    private readonly NumberField _numFfmpegRestartMb = new() { Minimum = 200, Maximum = 8000, Value = 600, Suffix = "MB" };
    private readonly NumberField _numSpeedDegradeSeconds = new() { Minimum = 5, Maximum = 120, Value = 20, Suffix = "s" };

    private StackPanel BuildAdvancedPage()
    {
        _cmbLogLevel.Items.AddRange(["Debug", "Information", "Warning", "Error"]);
        _cmbLogLevel.Width = 260;
        _numFfmpegWarnMb.Width = 120;
        _numFfmpegRestartMb.Width = 120;
        _numSpeedDegradeSeconds.Width = 120;

        var advanced = CreatePage("Advanced", "Troubleshooting tools, performance watchdog, and advanced options.");

        advanced.Controls.Add(Section("FFmpeg"));
        advanced.Controls.Add(Card(Row("FFmpeg location", "Leave empty to use the FFmpeg that ships with ScreenVault.", FfmpegEditor(), Glyphs.Folder)));

        advanced.Controls.Add(Section("Performance watchdog"));
        advanced.Controls.Add(Card(
            Row("Memory warning threshold", "Warn when FFmpeg memory consumption exceeds this value.", _numFfmpegWarnMb, Glyphs.Speed),
            Row("Memory restart threshold", "Restart recording pipeline if FFmpeg memory consumption exceeds this value.", _numFfmpegRestartMb, Glyphs.Refresh),
            Row("Slow-encoding detection duration", "Seconds of low encoding speed before frame rate is automatically lowered.", _numSpeedDegradeSeconds, Glyphs.Clock)));

        advanced.Controls.Add(Section("Troubleshooting"));
        var btnOpenLogs = new ModernButton("Open folder", ButtonKind.Secondary, Glyphs.FolderOpen) { Size = new Size(130, 32) };
        btnOpenLogs.Click += (_, _) => OpenLogsFolder();
        var btnExportDiagnostics = new ModernButton("Export…", ButtonKind.Secondary, Glyphs.Export) { Size = new Size(130, 32) };
        btnExportDiagnostics.Click += (_, _) => ExportDiagnostics();
        advanced.Controls.Add(Card(
            Row("Log detail", "Use Debug only while investigating a problem.", _cmbLogLevel, Glyphs.Diagnostic),
            Row("Log files", "Open the folder that contains ScreenVault's logs.", btnOpenLogs),
            Row("Diagnostics package", "Creates a zip with logs, settings and recent sessions on your Desktop.", btnExportDiagnostics)));

        advanced.Controls.Add(Section("Maintenance"));
        var btnRerunWizard = new ModernButton("Run wizard…", ButtonKind.Secondary) { Size = new Size(130, 32) };
        btnRerunWizard.Click += (_, _) => RerunWizard();
        var btnResetDefaults = new ModernButton("Reset…", ButtonKind.Destructive, Glyphs.Refresh) { Size = new Size(130, 32) };
        btnResetDefaults.Click += (_, _) => ResetAllDefaults();
        advanced.Controls.Add(Card(
            Row("Setup wizard", "Walk through storage, audio check and startup options again.", btnRerunWizard),
            Row("Reset all settings", "Restore every setting to its default value (recordings are not touched).", btnResetDefaults)));

        return advanced;
    }

    private FlowLayoutPanel FfmpegEditor()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _txtFfmpegPath.Width = 220;
        var btnBrowse = new ModernButton("Browse…", ButtonKind.Secondary, Glyphs.FolderOpen) { Size = new Size(100, 32) };
        btnBrowse.Click += (_, _) => BrowseFfmpeg();
        panel.Controls.Add(_txtFfmpegPath);
        panel.Controls.Add(btnBrowse);
        return panel;
    }

    private void BrowseFfmpeg()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _txtFfmpegPath.Text = dlg.FileName;
        }
    }

    private void LoadAdvancedSettings(AppSettings s)
    {
        _txtFfmpegPath.Text = s.Advanced.FfmpegPath ?? string.Empty;
        _cmbLogLevel.SelectedItem = s.Advanced.LogLevel;
        if (_cmbLogLevel.SelectedIndex < 0)
        {
            _cmbLogLevel.SelectedIndex = 1;
        }

        _numFfmpegWarnMb.Value = Math.Clamp(s.Advanced.FfmpegMemoryWarnMb, 100, 4000);
        _numFfmpegRestartMb.Value = Math.Clamp(s.Advanced.FfmpegMemoryRestartMb, 200, 8000);
        _numSpeedDegradeSeconds.Value = Math.Clamp(s.Advanced.SpeedDegradeSeconds, 5, 120);
    }

    private void SaveAdvancedSettings(AppSettings s)
    {
        s.Advanced.FfmpegPath = string.IsNullOrWhiteSpace(_txtFfmpegPath.Text) ? null : _txtFfmpegPath.Text.Trim();
        s.Advanced.LogLevel = _cmbLogLevel.SelectedItem?.ToString() ?? "Information";
        s.Advanced.FfmpegMemoryWarnMb = (int)_numFfmpegWarnMb.Value;
        s.Advanced.FfmpegMemoryRestartMb = (int)_numFfmpegRestartMb.Value;
        s.Advanced.SpeedDegradeSeconds = (int)_numSpeedDegradeSeconds.Value;
    }

    private static void OpenLogsFolder()
    {
        var logsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenVault", "logs");
        Directory.CreateDirectory(logsFolder);
        Process.Start(new ProcessStartInfo { FileName = logsFolder, UseShellExecute = true });
    }

    private void RerunWizard()
    {
        if (_audioEngine == null)
        {
            ModernDialog.Info(this, "Audio engine is not available", "The setup wizard needs the audio engine to check your microphone.");
            return;
        }

        using var wizard = new FirstRunWizardForm(_settingsService, _audioEngine);
        if (wizard.ShowDialog(this) == DialogResult.OK)
        {
            _workingCopy = CloneSettings(_settingsService.Current);
            LoadSettingsIntoUi();
        }

        ShowPage(_nav.SelectedIndex);
    }

    private void ExportDiagnostics()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
            var zipPath = Path.Combine(desktop, $"ScreenVault_Diagnostics_{timestamp}.zip");

            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenVault");

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var logsDir = Path.Combine(appData, "logs");
                if (Directory.Exists(logsDir))
                {
                    foreach (var file in Directory.GetFiles(logsDir, "*.log").OrderByDescending(File.GetLastWriteTime).Take(5))
                    {
                        zip.CreateEntryFromFile(file, Path.Combine("logs", Path.GetFileName(file)));
                    }
                }

                var settingsFile = Path.Combine(appData, "settings.json");
                if (File.Exists(settingsFile))
                {
                    zip.CreateEntryFromFile(settingsFile, "settings.json");
                }
            }

            ModernDialog.Info(this, "Diagnostics Exported", $"Diagnostics package created:\n{zipPath}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export diagnostics.");
            ModernDialog.Error(this, "Export Failed", $"Could not create diagnostics zip:\n{ex.Message}");
        }
    }

    private void ResetAllDefaults()
    {
        if (ModernDialog.Confirm(this, "Reset all settings?",
            "Every setting returns to its default value when you save. Your recordings are not affected.",
            "Reset", "Cancel", destructive: true, icon: MessageBoxIcon.Warning))
        {
            _workingCopy = AppSettings.CreateDefault();
            LoadSettingsIntoUi();
        }
    }
}
