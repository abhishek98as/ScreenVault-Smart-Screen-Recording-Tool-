<div align="center">

<img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4?style=for-the-badge&logo=windows&logoColor=white" />
<img src="https://img.shields.io/badge/.NET-9.0%20%7C%2010.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" />
<img src="https://img.shields.io/badge/License-GPLv3-green?style=for-the-badge" />
<img src="https://img.shields.io/github/v/release/abhishek98as/ScreenVault-Smart-Screen-Recording-Tool-?style=for-the-badge&color=orange" />
<img src="https://img.shields.io/badge/Status-Active-brightgreen?style=for-the-badge" />

# 🎥 ScreenVault

### **Always-On, Crash-Proof Screen & Meeting Recorder for Windows**

*Never lose a recording again — ScreenVault captures your screen, mic, and system audio continuously from login to logout with military-grade reliability.*

<br/>

## ⬇️ Download

| Version | Platform | Size |
|---------|----------|------|
| [**ScreenVault-Setup.exe** *(Latest Release)*](https://github.com/abhishek98as/ScreenVault-Smart-Screen-Recording-Tool-/releases/latest) | Windows 10/11 x64 | ~50 MB |

> **No admin rights required.** Installs per-user, starts automatically at login.

</div>

---

## ✨ Why ScreenVault?

Most screen recorders stop when your headset disconnects, crash when the GPU driver resets, or lose your footage when the disk fills up. **ScreenVault doesn't.**

It was built for developers, remote workers, and anyone who needs a reliable, always-on recording that just works — no babysitting required.

---

## 🚀 Key Features

### 🛡️ Crash-Proof Recording Pipeline
Video and audio are written as continuous **MPEG-TS streams** with **1-second durable disk flushes**. If the app crashes, GPU driver resets, or power fails — at most ~1 second of video is lost. Period.

### ⚡ GPU-Accelerated Screen Capture
Zero-copy desktop capture via Windows **Desktop Duplication API** (`ddagrab`) with hardware encoding support:
- **NVIDIA** → NVENC
- **Intel** → Quick Sync (QSV)
- **AMD** → AMF
- **Fallback** → Software x264

### 🎙️ Dual-Device Audio Mixing
Mixes microphone (`WASAPI client`) and system audio (`WASAPI loopback`) in a single **48 kHz / 32-bit float pipeline** with soft limiting — preventing drift or desync across multi-hour sessions.

### 🔄 Seamless Device Failover
Unplug a Bluetooth headset, answer a Teams call, or switch your default audio endpoint — ScreenVault dynamically reconciles within **0.5 seconds** without interrupting the recording.

### ✂️ Keyframe-Aligned File Splitting
Video is automatically split every **10 minutes at IDR keyframes**. Every segment can be played independently in any media player. No corrupted files at boundaries.

### 🗜️ Lossless Background Remuxing
Completed `.ts` segments are silently remuxed to `.mkv` (or `.mp4`) in the background with **zero quality loss**, verified with `ffprobe` before the source stream is discarded.

### 💾 Multi-Location Storage Failover
Continuously monitors disk space. Automatically fails over to a secondary or backup location if the primary drive is full or disconnected.

### 📍 Instant Markers & Chapters
Press `Ctrl+Alt+Shift+M` to drop a timestamped note mid-meeting. Notes are saved to `markers.txt` and **automatically embedded as chapters** in your MKV files.

### 🔒 100% Offline — Zero Telemetry
ScreenVault makes **no network requests**, collects no data, and operates entirely offline. Your recordings stay yours.

---

## 🖥️ System Requirements

| Requirement | Details |
|-------------|---------|
| **OS** | Windows 10 (v1809+) or Windows 11, 64-bit |
| **Runtime** | Self-contained — .NET 9 runtime is **bundled** |
| **GPU** | Any DirectX 11 capable GPU (NVIDIA / AMD / Intel) |
| **Disk** | ~250 MB for app + bundled FFmpeg |
| **Admin** | ❌ Not required |

---

## 📦 Installation

### Option 1: Installer *(Recommended)*
1. Download **[ScreenVault-Setup.exe](https://github.com/abhishek98as/ScreenVault-Smart-Screen-Recording-Tool-/releases/latest)** from the Releases page.
2. Run the installer — no admin required. Installs to `%LOCALAPPDATA%\Programs\ScreenVault`.
3. Optionally enable **Start with Windows** to record automatically from login.

### Option 2: Portable Mode
1. Download and extract the **ScreenVault-Portable.zip** from Releases.
2. Run `ScreenVault.exe --portable`.
3. All settings and sessions are stored in the application folder (great for USB drives).

---

## 🎮 Tray Icon Guide

ScreenVault lives quietly in your system tray next to the clock:

| Icon | State | Meaning |
|------|-------|---------|
| 🔴 Solid Red Dot | **Recording** | Healthy — frames and audio are reaching disk |
| 🔴⚠️ Red Dot + Warning | **Recording (Degraded)** | Active, but using backup storage or fallback device |
| 🟡 Yellow Bars | **Paused** | Recording paused by user |
| 🔴↻ Red Dot + Arrow | **Starting / Recovering** | Initializing hardware or recovering from device switch |
| ⚪ Hollow Gray Circle | **Idle** | Running but not recording |
| 🔴❗ Red Circle with ! | **Faulted** | Hardware/encoder error — retrying automatically |

- **Left-Click**: Toggle the live Status Window (VU meters, disk gauges, elapsed time)
- **Right-Click**: Open context menu (recording controls, storage folders, audio device selectors)

---

## ⌨️ Global Hotkeys

| Hotkey | Action |
|--------|--------|
| `Ctrl+Alt+Shift+M` | Add Marker (saves note to `markers.txt` & MKV chapters) |
| `Ctrl+Alt+Shift+P` | Pause / Resume recording |
| `Ctrl+Alt+Shift+S` | Toggle Status Window |

> All hotkeys are fully customizable in **Settings → Hotkeys**.

---

## 🖱️ Command-Line Interface

ScreenVault supports full remote control and automation via named pipe IPC:

```bash
# Start / stop recording
ScreenVault.exe --startrecording
ScreenVault.exe --stoprecording

# Pause / resume
ScreenVault.exe --pause
ScreenVault.exe --resume

# Add a timestamped marker
ScreenVault.exe --marker "Discussion on database architecture"

# Query live status (outputs JSON)
ScreenVault.exe --status

# Open Settings dialog
ScreenVault.exe --settings

# Gracefully stop recording and exit
ScreenVault.exe --exit

# Run in portable mode
ScreenVault.exe --portable
```

When another instance is already running, commands are **automatically forwarded via IPC** — no second process is spawned.

---

## 📁 File Storage Layout

Default recording location: `%USERPROFILE%\Videos\Screen Recordings\`

```
Videos\
└── Screen Recordings\
    └── 2026-09-27\
        ├── SV_2026-09-27_09-01-12_p001.mkv    ← Lossless 10-min segment
        ├── SV_2026-09-27_09-01-12_p002.mkv
        ├── SV_2026-09-27_09-01-12_p003.mkv
        ├── markers.txt                          ← Plain-text timestamped markers
        └── session.json                         ← Session manifest & hardware metrics
```

### Merging Segments
To merge all segments of a session into one file with chapters:

```bash
# Right-click tray → Recent Sessions → Merge
# OR manually with FFmpeg:
ffmpeg -f concat -safe 0 -i list.ffconcat -map 0 -c copy Full_Session.mkv
```

---

## 🏗️ Architecture

```
ScreenVault/
├── src/
│   ├── ScreenVault.App/          # WinForms tray app, UI, IPC, CLI
│   │   ├── TrayApplicationContext.cs
│   │   ├── Program.cs
│   │   ├── Platform/             # StartWithWindows, RestartManagerWindow
│   │   ├── UI/                   # SettingsForm, StatusWindow, MarkerDialog
│   │   └── Ipc/                  # Named pipe server/client
│   └── ScreenVault.Core/         # Pure business logic (no UI dependencies)
│       ├── Recording/            # RecordingEngine, ResumeStateService
│       ├── Audio/                # WASAPI capture, mixer, pump
│       ├── Ffmpeg/               # FFmpeg process management
│       ├── Storage/              # StorageManager, failover logic
│       ├── PostProcessing/       # Background remuxer
│       ├── Sessions/             # Session manifest, chapter writing
│       └── Settings/             # AppSettings, InstallDefaultsService
├── tests/                        # xUnit test suite
├── installer/                    # Inno Setup installer script
├── build/                        # Release pipeline (build.ps1)
└── docs/                         # Architecture & deployment documentation
```

---

## 🔧 Building from Source

### Prerequisites
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download) or newer
- Windows 10/11 x64
- *(Optional)* [Inno Setup 6](https://jrsoftware.org/isinfo.php) for building the installer

```bash
# 1. Clone the repo
git clone https://github.com/abhishek98as/ScreenVault-Smart-Screen-Recording-Tool-.git
cd "ScreenVault-Smart-Screen-Recording-Tool-"

# 2. Build
dotnet build -c Release

# 3. Run tests
dotnet test -c Release

# 4. Publish (self-contained, single-file exe)
dotnet publish src/ScreenVault.App -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishReadyToRun=true \
  -o publish/

# 5. Build full installer (requires Inno Setup 6 in PATH)
.\build\build.ps1 -Version "1.0.0"
# → Produces: dist/ScreenVault-1.0.0-Setup.exe
```

---

## 🛠️ Troubleshooting

### Antivirus / Windows Defender False Positives
ScreenVault captures desktop frames and bundles `ffmpeg.exe`. Some heuristic scanners may flag this. ScreenVault contains **no telemetry, no network connections, and no malicious code**. To resolve: add `%LOCALAPPDATA%\Programs\ScreenVault` to your antivirus exclusions.

### Microphone Appears Silent
Ensure Windows microphone access is enabled:
1. Open **Settings → Privacy & Security → Microphone**
2. Enable **Microphone access** and **Let desktop apps access your microphone**

### High CPU Usage
If software encoding is being used (no compatible GPU), CPU usage will be higher. Install the latest GPU drivers to enable hardware encoding (NVENC/QSV/AMF).

---

## 🤝 Contributing

Contributions are welcome! Please:
1. Fork the repo and create a feature branch
2. Ensure all tests pass: `dotnet test`
3. Open a Pull Request with a clear description of the change

---

## 📄 License

ScreenVault is free and open-source software licensed under the **[GNU General Public License v3.0](LICENSE)**.

Third-party component notices are documented in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

---

<div align="center">

**Made with ❤️ for developers and remote workers who can't afford to lose a recording.**

⭐ *If ScreenVault saved your work, please star this repo!*

</div>
