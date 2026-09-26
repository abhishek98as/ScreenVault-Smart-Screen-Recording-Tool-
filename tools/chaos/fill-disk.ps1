<#
.SYNOPSIS
    ScreenVault Chaos Test Tool: Simulates low disk space or disk full conditions.
.DESCRIPTION
    Creates dummy zero-byte or sparse files in a target drive or folder to simulate
    low disk conditions and trigger ScreenVault's automatic storage failover.
.PARAMETER TargetPath
    Path or drive letter to fill (e.g. C:\TempFill or D:\TempFill).
.PARAMETER TargetFreeMb
    Desired remaining free space in MB on the target volume.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetPath,

    [int]$TargetFreeMb = 800
)

$targetDir = [System.IO.Path]::GetFullPath($TargetPath)
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

$driveRoot = [System.IO.Path]::GetPathRoot($targetDir)
$drive = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$($driveRoot.TrimEnd('\'))'"
if (-not $drive) {
    Write-Error "Could not query drive info for root $driveRoot"
    exit 1
}

$currentFreeMb = [math]::Round($drive.FreeSpace / 1MB, 2)
Write-Host "Current free space on $driveRoot is $currentFreeMb MB"

$bytesToFill = $drive.FreeSpace - ($TargetFreeMb * 1MB)
if ($bytesToFill -le 0) {
    Write-Host "Drive already has <= $TargetFreeMb MB free. No action needed."
    exit 0
}

Write-Host "Creating dummy file of $([math]::Round($bytesToFill / 1MB, 2)) MB at $targetDir\chaos_filler.bin..."
$fillFile = Join-Path $targetDir "chaos_filler.bin"

# Use fsutil to allocate instantly without slow zeroing
& fsutil file createnew $fillFile $bytesToFill

Write-Host "Done. Remaining free space: $([math]::Round((Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($driveRoot.TrimEnd('\'))'").FreeSpace / 1MB, 2)) MB"
Write-Host "To clean up later, run: Remove-Item '$fillFile' -Force"
