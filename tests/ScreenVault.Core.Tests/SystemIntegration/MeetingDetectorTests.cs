using ScreenVault.Core.SystemIntegration;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.SystemIntegration;

public sealed class MeetingDetectorTests
{
    [Theory]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\C:#Users#John#AppData#Local#Microsoft#Teams#current#Teams.exe", "Microsoft Teams")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\C:#Users#John#AppData#Roaming#Zoom#bin#Zoom.exe", "Zoom")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\C:#Program Files#Google#Chrome#Application#chrome.exe", "Google Chrome")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\C:#Program Files (x86)#Microsoft#Edge#Application#msedge.exe", "Microsoft Edge")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\C:#Users#John#AppData#Local#slack#slack.exe", "Slack")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\C:#Users#John#AppData#Local#Discord#app-1.0.9000#Discord.exe", "Discord")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\C:#CustomTools#MyCustomMeetingTool.exe", "MyCustomMeetingTool")]
    public void ResolveFriendlyName_IdentifiesApplicationsCorrectly(string keyPath, string expectedName)
    {
        var resolved = MeetingDetector.ResolveFriendlyName(keyPath);
        resolved.ShouldBe(expectedName);
    }
}
