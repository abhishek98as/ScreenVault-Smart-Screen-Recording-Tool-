<#
.SYNOPSIS
    Builds, tests, publishes and packages ScreenVault into a Windows installer.
.DESCRIPTION
    Executes the release pipeline:
    1. Reads version from Directory.Build.props or -Version argument.
    2. Runs test suite with dotnet test -c Release.
    3. Publishes self-contained folder publish (ReadyToRun, win-x64).
    4. Bundles FFmpeg tools, licenses, and notices.
    5. Cleans any transient files (settings.json, install-defaults.json, logs).
    6. Optionally signs binaries.
    7. Compiles the installer using Inno Setup 6.7+ or 7.x (ISCC.exe).
    8. Generates SHA-256 checksums.
.PARAMETER Version
    The version number to build (e.g. 1.2.0). If omitted, read from Directory.Build.props.
.PARAMETER SkipTests
    Skips running the test suite.
.PARAMETER Sign
    Enables code signing using signtool.exe.
.PARAMETER CertPath
    Path to PFX certificate for code signing.
.PARAMETER CertPassword
    Password for PFX certificate (or read from $env:CODE_SIGN_PASSWORD).
#>

[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$Sign,
    [string]$CertPath,
    [string]$CertPassword
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path

# 1. Version Detection
if (-not $Version) {
    $propsPath = Join-Path $repoRoot "Directory.Build.props"
    if (Test-Path $propsPath) {
        $propsXml = [xml](Get-Content $propsPath)
        $Version = $propsXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    }
}
if (-not $Version) {
    $Version = "1.2.0"
}

Write-Host "=== ScreenVault Build & Package v$Version ===" -ForegroundColor Cyan

# 2. Locate Inno Setup Compiler (ISCC.exe)
# The installer uses the modern light/dark wizard, which needs Inno Setup 6.7+ (7.x recommended).
$isccCandidates = @(
    (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)

$isccPath = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $isccPath) {
    throw "Inno Setup compiler (ISCC.exe) was not found in PATH or standard installation directories. Please install Inno Setup 6.7 or newer (7.x recommended) from https://jrsoftware.org/isdl.php"
}
Write-Host "Found Inno Setup Compiler: $isccPath" -ForegroundColor Gray

# 3. Automated Tests
if (-not $SkipTests) {
    Write-Host "`n--> [1/5] Running tests in Release mode..." -ForegroundColor Cyan
    & dotnet test (Join-Path $repoRoot "ScreenVault.sln") -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Test suite failed with exit code $LASTEXITCODE. Stopping build."
    }
} else {
    Write-Host "`n--> [1/5] Skipping tests as requested." -ForegroundColor Yellow
}

$publishDir = Join-Path $repoRoot "publish"
$installerOutDir = Join-Path $repoRoot "dist"

Write-Host "`n--> [2/5] Preparing output directories..." -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $installerOutDir | Out-Null

# 5. Publish Self-Contained Folder
Write-Host "`n--> [3/5] Publishing self-contained folder publish (ReadyToRun, win-x64)..." -ForegroundColor Cyan
$appProj = Join-Path $repoRoot "src\ScreenVault.App\ScreenVault.App.csproj"
& dotnet publish $appProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# 6. Copy Bundled FFmpeg, Licenses, and Notices
Write-Host "`n--> [4/5] Bundling FFmpeg tools and third-party notices..." -ForegroundColor Cyan
$publishFfmpegDir = Join-Path $publishDir "ffmpeg"
New-Item -ItemType Directory -Force -Path $publishFfmpegDir | Out-Null

$ffmpegSrcDir = Join-Path $repoRoot "tools\ffmpeg"
if (Test-Path $ffmpegSrcDir) {
    # ffplay is not shipped: recordings open in the user's own video app.
    Copy-Item "$ffmpegSrcDir\*" $publishFfmpegDir -Recurse -Force -Exclude "ffplay.exe"
    Remove-Item (Join-Path $publishFfmpegDir "ffplay.exe") -Force -ErrorAction SilentlyContinue
} else {
    Write-Warning "FFmpeg source directory not found at $ffmpegSrcDir."
}

$noticesSrc = Join-Path $repoRoot "THIRD_PARTY_NOTICES.txt"
if (Test-Path $noticesSrc) {
    Copy-Item $noticesSrc $publishDir -Force
}

# Ensure no temporary or runtime configuration files exist in publish directory
$forbiddenFiles = @(
    (Join-Path $publishDir "install-defaults.json"),
    (Join-Path $publishDir "settings.json"),
    (Join-Path $publishDir "settings.json.bak")
)
foreach ($f in $forbiddenFiles) {
    if (Test-Path $f) {
        Remove-Item $f -Force
    }
}
$publishLogs = Join-Path $publishDir "logs"
if (Test-Path $publishLogs) {
    Remove-Item $publishLogs -Recurse -Force
}

# Optional Signing of Application Executable
if ($Sign) {
    $pfxPass = if ($CertPassword) { $CertPassword } else { $env:CODE_SIGN_PASSWORD }
    if ($CertPath -and (Test-Path $CertPath)) {
        Write-Host "Signing ScreenVault.exe..." -ForegroundColor Cyan
        & signtool sign /fd SHA256 /f $CertPath /p $pfxPass /tr http://timestamp.digicert.com /td SHA256 (Join-Path $publishDir "ScreenVault.exe")
    }
}

# 7. Compile Inno Setup Script
Write-Host "`n--> [5/5] Compiling Inno Setup installer..." -ForegroundColor Cyan
$issPath = Join-Path $repoRoot "installer\ScreenVault.iss"
& $isccPath "/DAppVersion=$Version" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

# Optional Signing of Final Installer Executable
$installerExe = Join-Path $installerOutDir "ScreenVault_Setup_$Version.exe"
if ($Sign -and (Test-Path $installerExe)) {
    $pfxPass = if ($CertPassword) { $CertPassword } else { $env:CODE_SIGN_PASSWORD }
    if ($CertPath -and (Test-Path $CertPath)) {
        Write-Host "Signing Setup executable..." -ForegroundColor Cyan
        & signtool sign /fd SHA256 /f $CertPath /p $pfxPass /tr http://timestamp.digicert.com /td SHA256 $installerExe
    }
}

# 8. Compute Checksum
if (Test-Path $installerExe) {
    $fileItem = Get-Item $installerExe
    $hash = (Get-FileHash $installerExe -Algorithm SHA256).Hash
    $shaFile = "$installerExe.sha256.txt"
    "$hash  $($fileItem.Name)" | Set-Content $shaFile -Encoding UTF8

    Write-Host "`n========================================================" -ForegroundColor Green
    Write-Host "BUILD SUCCESSFUL!" -ForegroundColor Green
    Write-Host "Installer: $installerExe" -ForegroundColor Green
    Write-Host "Size:      $([math]::Round($fileItem.Length / 1MB, 2)) MB" -ForegroundColor Green
    Write-Host "SHA256:    $hash" -ForegroundColor Green
    Write-Host "Checksum:  $shaFile" -ForegroundColor Green
    Write-Host "========================================================`n" -ForegroundColor Green
} else {
    throw "Expected installer executable not found at $installerExe."
}
