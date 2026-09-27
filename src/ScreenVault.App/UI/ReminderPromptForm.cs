using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.SystemIntegration;

namespace ScreenVault.App.UI;

/// <summary>Toast reminding the user that nothing is being recorded during work hours.</summary>
public sealed class ReminderPromptForm : ToastForm
{
    public ReminderPromptForm(
        Action onStartRecording,
        IReminderService reminderService)
        : base(Glyphs.Clock, "ScreenVault isn't recording", "Would you like to start recording now?")
    {
        ArgumentNullException.ThrowIfNull(onStartRecording);
        ArgumentNullException.ThrowIfNull(reminderService);

        Text = "ScreenVault Reminder";
        AddAction("Start recording", ButtonKind.Record, onStartRecording, Glyphs.Record);
        AddAction("Snooze 1 hour", ButtonKind.Secondary, () => reminderService.Snooze(TimeSpan.FromHours(1)));
        AddAction("Not today", ButtonKind.Subtle, reminderService.SnoozeForToday);
    }
}
