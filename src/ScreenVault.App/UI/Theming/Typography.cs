using System.Collections.Concurrent;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Serilog;

namespace ScreenVault.App.UI.Theming;

/// <summary>
/// Shared type ramp (Segoe UI, as used by Windows 11). Fonts are created once and reused;
/// sizes are in points so they follow the display scaling automatically.
/// </summary>
public static class Typography
{
    private const string Regular = "Segoe UI";
    private const string Semibold = "Segoe UI Semibold";
    private const string Semilight = "Segoe UI Semilight";

    private static readonly Lazy<HashSet<string>> InstalledFamilies = new(LoadInstalledFamilies);
    private static readonly ConcurrentDictionary<string, Font> Cache = new(StringComparer.Ordinal);

    /// <summary>9 pt — default body text.</summary>
    public static Font Body => Get(Regular, 9f, FontStyle.Regular);

    /// <summary>9 pt semibold — emphasized body text, button labels.</summary>
    public static Font BodyStrong => GetSemibold(9f);

    /// <summary>8 pt — captions, hints, secondary metadata.</summary>
    public static Font Caption => Get(Regular, 8f, FontStyle.Regular);

    /// <summary>8 pt semibold — column headers, small labels.</summary>
    public static Font CaptionStrong => GetSemibold(8f);

    /// <summary>10.5 pt semibold — card and section titles.</summary>
    public static Font Subtitle => GetSemibold(10.5f);

    /// <summary>13 pt semibold — dialog titles.</summary>
    public static Font Title => GetSemibold(13f);

    /// <summary>18 pt semibold — page headings.</summary>
    public static Font Display => GetSemibold(18f);

    /// <summary>Large light numerals for the recording timer.</summary>
    public static Font Timer => Get(Semilight, 24f, FontStyle.Regular);

    public static Font GetSemibold(float size)
    {
        return IsInstalled(Semibold)
            ? Get(Semibold, size, FontStyle.Regular)
            : Get(Regular, size, FontStyle.Bold);
    }

    public static Font Get(string family, float size, FontStyle style)
    {
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{family}|{size}|{(int)style}");
        return Cache.GetOrAdd(key, _ =>
        {
            var resolved = IsInstalled(family) ? family : Regular;
            return new Font(resolved, size, style, GraphicsUnit.Point);
        });
    }

    public static bool IsInstalled(string family) => InstalledFamilies.Value.Contains(family);

    private static HashSet<string> LoadInstalledFamilies()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var collection = new InstalledFontCollection();
            foreach (var fontFamily in collection.Families)
            {
                set.Add(fontFamily.Name);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            Log.Debug(ex, "Could not enumerate installed fonts.");
        }

        return set;
    }
}
