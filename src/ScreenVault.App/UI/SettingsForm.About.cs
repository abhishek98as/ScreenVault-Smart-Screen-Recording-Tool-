using ScreenVault.App.Platform;
using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Settings;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private StackPanel BuildAboutPage()
    {
        var about = CreatePage("About", "Version and install details.");
        about.Controls.Add(AboutCard());
        return about;
    }

    private void LoadAboutSettings(AppSettings s)
    {
        // About page has no user-configurable settings to load
    }

    private void SaveAboutSettings(AppSettings s)
    {
        // About page has no user-configurable settings to save
    }

    private CardPanel AboutCard()
    {
        var info = GetAboutInfo();
        var card = new CardPanel { Padding = new Padding(0), Spacing = 0, Dividers = true };

        var hero = new Panel { Height = 104 };
        var appIcon = AppIcon.Get();
        if (appIcon != null)
        {
            using var large = new Icon(appIcon, 64, 64);
            hero.Controls.Add(new PictureBox { Image = large.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(20, 20, 64, 64) });
        }

        hero.Controls.Add(new TextLabel("ScreenVault", Typography.Title) { Location = new Point(100, 24) });
        hero.Controls.Add(new TextLabel($"Version {info.Version}", Typography.Body, TextTone.Secondary) { Location = new Point(100, 52) });
        hero.Controls.Add(new TextLabel("Always-on, crash-proof screen & meeting recorder", Typography.Caption, TextTone.Tertiary) { Location = new Point(100, 74) });
        card.Controls.Add(hero);

        card.Controls.Add(Row("Install type", null, Value(info.Scope), Glyphs.Shield));
        var pathValue = Value(info.InstallPath);
        _toolTip.SetToolTip(pathValue, info.InstallPath);
        card.Controls.Add(Row("Install location", null, pathValue, Glyphs.Folder));
        card.Controls.Add(Row("FFmpeg", "Bundled encoder (GPL). See THIRD_PARTY_NOTICES.txt.", Value(info.FfmpegVersion), Glyphs.Video));
        card.Controls.Add(Row("Privacy", "ScreenVault never connects to the internet. Your recordings stay on your drives and the tray icon is always visible while recording.", null, Glyphs.Lock));
        return card;

        static TextLabel Value(string text) => new(text, Typography.Body, TextTone.Secondary) { AutoSize = false, AutoEllipsis = true, Size = new Size(320, 20), TextAlign = ContentAlignment.MiddleRight };
    }

    private static (string Version, string Scope, string InstallPath, string FfmpegVersion) GetAboutInfo()
    {
        var version = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "1.3.0";
        var installPath = AppContext.BaseDirectory.TrimEnd('\\');

        string scope = "Per-user";
        if (StartWithWindows.HasHklmRunEntry() ||
            installPath.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
        {
            scope = "All users (machine)";
        }
        else
        {
            var defaultsPath = Path.Combine(AppContext.BaseDirectory, "install-defaults.json");
            if (File.Exists(defaultsPath))
            {
                try
                {
                    var defaults = InstallDefaultsService.LoadDefaults(defaultsPath, new System.IO.Abstractions.FileSystem());
                    if (!string.IsNullOrEmpty(defaults?.InstallScope))
                    {
                        scope = defaults.InstallScope;
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }

        string ffmpegVersion = "ffmpeg (bundled)";
        var ffmpegVersionPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "VERSION.txt");
        if (File.Exists(ffmpegVersionPath))
        {
            try
            {
                var firstLine = File.ReadAllLines(ffmpegVersionPath).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrEmpty(firstLine))
                {
                    ffmpegVersion = firstLine.Trim();
                }
            }
            catch
            {
                // Ignore
            }
        }

        return (version, scope, installPath, ffmpegVersion);
    }
}
