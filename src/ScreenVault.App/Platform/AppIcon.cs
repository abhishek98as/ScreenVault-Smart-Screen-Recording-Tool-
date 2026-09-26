namespace ScreenVault.App.Platform;

public static class AppIcon
{
    private static Icon? _icon;
    private static readonly object Lock = new();

    public static Icon? Get()
    {
        lock (Lock)
        {
            if (_icon != null) return _icon;

            var localPath = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");
            if (File.Exists(localPath))
            {
                try
                {
                    _icon = new Icon(localPath);
                    return _icon;
                }
                catch
                {
                    // Fallback
                }
            }

            try
            {
                var processPath = Environment.ProcessPath ?? Application.ExecutablePath;
                if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
                {
                    _icon = Icon.ExtractAssociatedIcon(processPath);
                }
            }
            catch
            {
                // Fallback
            }

            return _icon;
        }
    }
}
