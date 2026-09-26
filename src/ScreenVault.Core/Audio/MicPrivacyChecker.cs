using Microsoft.Win32;

namespace ScreenVault.Core.Audio;

public static class MicPrivacyChecker
{
    private const string GlobalConsentPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string NonPackagedPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged";

    public static bool IsMicrophoneAccessAllowed()
    {
        try
        {
            using var globalKey = Registry.CurrentUser.OpenSubKey(GlobalConsentPath);
            if (globalKey != null)
            {
                var globalValue = globalKey.GetValue("Value") as string;
                if (string.Equals(globalValue, "Deny", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            using var nonPackagedKey = Registry.CurrentUser.OpenSubKey(NonPackagedPath);
            if (nonPackagedKey != null)
            {
                var nonPackagedValue = nonPackagedKey.GetValue("Value") as string;
                if (string.Equals(nonPackagedValue, "Deny", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // If registry cannot be queried, assume allowed
            return true;
        }
    }
}
