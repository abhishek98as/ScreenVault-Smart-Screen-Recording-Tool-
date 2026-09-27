using Microsoft.Win32;
using ScreenVault.Core.Settings;
using Serilog;

namespace ScreenVault.App.UI.Theming;

/// <summary>Implemented by controls that pull their colors from <see cref="Theme.Current"/>.</summary>
public interface IThemeAware
{
    void ApplyTheme();
}

/// <summary>
/// Resolves the active <see cref="Palette"/> from the user's appearance setting and the
/// Windows "app mode" (light/dark) or high-contrast preference, and notifies open windows
/// when it changes so the whole UI can switch live without a restart.
/// </summary>
public static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static AppThemeMode _mode = AppThemeMode.System;

    public static Palette Current { get; private set; } = Palette.Light;

    public static AppThemeMode Mode => _mode;

    /// <summary>Raised on the UI thread after <see cref="Current"/> changed.</summary>
    public static event EventHandler? Changed;

    public static void Initialize(AppThemeMode mode)
    {
        _mode = mode;
        Current = Resolve(mode);
        Log.Information("UI theme initialized: mode={Mode}, palette={Palette}", mode, Current.Name);
    }

    public static void SetMode(AppThemeMode mode)
    {
        _mode = mode;
        Refresh();
    }

    /// <summary>Re-evaluates the system preference; raises <see cref="Changed"/> if the palette differs.</summary>
    public static void Refresh()
    {
        var next = Resolve(_mode);
        var changed = next.IsHighContrast || Current.IsHighContrast
            ? next.IsHighContrast != Current.IsHighContrast || next.Window != Current.Window || next.Text != Current.Text
            : !ReferenceEquals(next, Current);

        if (!changed)
        {
            return;
        }

        Current = next;
        Log.Information("UI theme changed to {Palette}", next.Name);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Applies the current palette to every <see cref="IThemeAware"/> control in the tree.</summary>
    public static void ApplyTree(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root is IThemeAware aware)
        {
            aware.ApplyTheme();
        }

        foreach (Control child in root.Controls)
        {
            ApplyTree(child);
        }
    }

    public static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Debug(ex, "Could not read Windows app theme preference.");
            return false;
        }
    }

    private static Palette Resolve(AppThemeMode mode)
    {
        if (SystemInformation.HighContrast)
        {
            return Palette.HighContrast();
        }

        return mode switch
        {
            AppThemeMode.Light => Palette.Light,
            AppThemeMode.Dark => Palette.Dark,
            _ => SystemUsesDarkApps() ? Palette.Dark : Palette.Light
        };
    }
}
