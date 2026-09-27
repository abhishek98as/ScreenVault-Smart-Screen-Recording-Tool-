using ScreenVault.Core.Settings;

namespace ScreenVault.App.UI;

/// <summary>Shared confirmations so the tray menu and the status window ask the same way.</summary>
internal static class RecordingPrompts
{
    /// <summary>Asks before stopping (unless disabled). Offers "Don't ask me again".</summary>
    public static bool ConfirmStop(IWin32Window? owner, ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        if (!settingsService.Current.General.ConfirmBeforeStop)
        {
            return true;
        }

        var (confirmed, dontAskAgain) = ModernDialog.ConfirmWithOption(
            owner,
            "Stop recording?",
            "The recording will be saved. Anything that happens after this point won't be recorded.",
            "Stop & save",
            "Keep recording",
            "Don't ask me again",
            destructive: true);

        if (confirmed && dontAskAgain)
        {
            try
            {
                var settings = settingsService.Current.Clone();
                settings.General.ConfirmBeforeStop = false;
                settingsService.Save(settings);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(ex, "Could not save the \"don't ask again\" choice.");
            }
        }

        return confirmed;
    }

    public static bool ConfirmExitWhileRecording(IWin32Window? owner)
    {
        return ModernDialog.Confirm(
            owner,
            "Stop recording and exit?",
            "A recording is in progress. ScreenVault will stop, save it and close. Nothing will be recorded until you start ScreenVault again.",
            "Stop & exit",
            "Cancel",
            destructive: true,
            icon: MessageBoxIcon.Warning);
    }
}
