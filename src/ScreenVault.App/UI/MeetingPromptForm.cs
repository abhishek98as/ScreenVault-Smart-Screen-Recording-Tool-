using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI;

/// <summary>Toast shown when a meeting app starts using the microphone while ScreenVault is idle.</summary>
public sealed class MeetingPromptForm : ToastForm
{
    public MeetingPromptForm(
        string appName,
        Action onStartRecording,
        Action<string> onAlwaysForApp)
        : base(Glyphs.Microphone, $"{appName} is using your microphone", "Start recording this meeting?")
    {
        ArgumentNullException.ThrowIfNull(onStartRecording);
        ArgumentNullException.ThrowIfNull(onAlwaysForApp);

        Text = "ScreenVault – Meeting Detected";
        AddAction("Start recording", ButtonKind.Record, onStartRecording, Glyphs.Record);
        var always = AddAction("Always for this app", ButtonKind.Secondary, () =>
        {
            onAlwaysForApp(appName);
            onStartRecording();
        });
        AddAction("Not now", ButtonKind.Subtle, () => { });

        var tip = ModernToolTip.Create();
        tip.SetToolTip(always, $"Always start recording automatically when {appName} starts a meeting");
        Disposed += (_, _) => tip.Dispose();
    }
}
