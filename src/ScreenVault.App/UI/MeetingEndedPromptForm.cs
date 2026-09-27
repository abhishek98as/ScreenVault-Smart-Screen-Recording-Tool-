using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;

namespace ScreenVault.App.UI;

/// <summary>Toast shown when the call that started a recording has ended.</summary>
public sealed class MeetingEndedPromptForm : ToastForm
{
    public MeetingEndedPromptForm(string appName, Action onStopRecording)
        : base(Glyphs.Headphones, $"{appName} call ended", "Stop and save the recording?")
    {
        ArgumentNullException.ThrowIfNull(onStopRecording);

        Text = "ScreenVault – Call Ended";
        AddAction("Stop & save", ButtonKind.Strong, onStopRecording, Glyphs.Stop);
        AddAction("Keep recording", ButtonKind.Subtle, () => { });
    }
}
