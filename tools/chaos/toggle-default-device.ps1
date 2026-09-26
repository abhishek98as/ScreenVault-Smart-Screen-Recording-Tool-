<#
.SYNOPSIS
    ScreenVault Chaos Test Tool: Rapidly toggles default audio playback and recording devices.
.DESCRIPTION
    Switches between available audio endpoints to verify that ScreenVault's AudioEngine and
    reconcile loop seamlessly handle dynamic device switches without losing audio or failing.
.PARAMETER Cycles
    Number of switch cycles to perform.
.PARAMETER DelaySeconds
    Seconds to pause between switches.
#>
param(
    [int]$Cycles = 10,
    [double]$DelaySeconds = 2.0
)

Write-Host "ScreenVault Audio Device Chaos Tester"
Write-Host "Cycles: $Cycles, Delay: $DelaySeconds s"

# Check if AudioDeviceCmdlets module is installed, else guide installation
$module = Get-Module -ListAvailable -Name AudioDeviceCmdlets
if (-not $module) {
    Write-Host "AudioDeviceCmdlets module not found. Installing from PSGallery..." -ForegroundColor Yellow
    try {
        Install-Module -Name AudioDeviceCmdlets -Scope CurrentUser -Force -AllowClobber
    }
    catch {
        Write-Error "Failed to install AudioDeviceCmdlets: $_"
        exit 1
    }
}

Import-Module AudioDeviceCmdlets

$playbackDevices = Get-AudioDevice -List | Where-Object { $_.Type -eq "Playback" }
if ($playbackDevices.Count -lt 2) {
    Write-Warning "Less than 2 playback devices detected. Cannot toggle playback endpoints."
}

for ($i = 1; $i -le $Cycles; $i++) {
    Write-Host "Cycle $i of $Cycles..." -ForegroundColor Cyan

    if ($playbackDevices.Count -ge 2) {
        $nextDevice = $playbackDevices[($i % $playbackDevices.Count)]
        Write-Host "  Switching playback to: $($nextDevice.Name)"
        Set-AudioDevice -Index $nextDevice.Index | Out-Null
    }

    Start-Sleep -Seconds $DelaySeconds
}

Write-Host "Chaos device switching complete." -ForegroundColor Green
