# Architecture Decision Record: Video Encoder Probing & Fallback Hierarchy (PIPE-03)

## Status
Accepted and Implemented.

## Context
ScreenVault captures the screen using Direct3D 11 Desktop Duplication (`ddagrab`) and encodes the video stream into MPEG-TS packets via FFmpeg.
Different client machines feature diverse hardware configurations, including:
- NVIDIA dedicated GPUs (requiring `h264_nvenc`)
- AMD GPUs / APUs (requiring `h264_amf`)
- Intel integrated / discrete GPUs (requiring `h264_qsv`)
- Hybrid-GPU laptops (where display duplication runs on the integrated GPU or D3D11 device, while hardware encoding might run across PCIe or require system memory staging)
- Machines with outdated GPU drivers or software-only rendering, falling back to CPU encoding (`libx264`)

In earlier builds, running `libx264` at 30/29 fps on resource-constrained systems caused encoding speeds to drop below 1.0x (e.g. 0.97x), resulting in raw frame accumulation, pipe congestion, and excessive memory growth.

## Decision

### 1. Encoder Profile Hierarchy
ScreenVault evaluates encoder profiles in strict order of efficiency and hardware capability:
1. `nvenc-d3d11`: NVIDIA NVENC with zero-copy Direct3D 11 surface passing (`-hwaccel d3d11va -c:v h264_nvenc`).
2. `nvenc-sysmem`: NVIDIA NVENC with system memory staging (used when cross-adapter surface sharing fails).
3. `amf-d3d11`: AMD Advanced Media Framework (`h264_amf`) with Direct3D 11 surface passing.
4. `amf-sysmem`: AMD AMF with system memory buffer fallback.
5. `qsv-d3d11`: Intel Quick Sync Video (`h264_qsv`) with Direct3D 11.
6. `qsv-sysmem`: Intel Quick Sync Video with system memory staging.
7. `x264` (Software Fallback): CPU encoding using `libx264` with `-preset ultrafast -tune zerolatency -pix_fmt yuv420p`.

### 2. Probing Mechanism
- On application startup or when the user clicks **Re-detect** in `Settings -> Video`, `EncoderProber` tests each profile sequentially.
- Each probe executes a short 1-second synthetic probe command with `ffmpeg.exe`:
  - Input: synthetic test pattern or capture probe
  - Arguments: profile-specific acceleration flags and encoder options
  - Output: `-f null -`
- The prober captures:
  - Exit code
  - Execution duration (ms)
  - Last 20 lines of FFmpeg `stderr`
- Probing stops at the first successful profile, caching the chosen profile and storing the detailed diagnostics for all tested profiles.
- Settings UI displays a diagnostics table showing each profile with ✔ (Supported) or ✖ (Rejected with reason), plus a manual override dropdown.

### 3. Hybrid-GPU Laptop Support
On hybrid laptops (e.g., Intel iGPU driving the display panel and NVIDIA dGPU handling 3D workloads):
- Direct3D 11 desktop duplication (`ddagrab`) captures textures from the GPU driving the monitor (typically the integrated GPU).
- Direct zero-copy surface passing to an external dGPU encoder (`nvenc-d3d11`) can fail with DirectX adapter mismatch errors (`DXGI_ERROR_UNSUPPORTED` or initialization failure).
- The `*-sysmem` fallback profiles explicitly pull frames to system memory (`-vf "hwdownload,format=nv12"`) before submitting to the target hardware encoder, allowing NVENC or AMF to encode frames captured from a different physical adapter.

### 4. GPU Driver & Build Requirements
- Bundled FFmpeg build: Must be compiled with `--enable-nvenc --enable-amf --enable-libvpl` (or `qsv`) and `--enable-libx264`.
- NVIDIA: Minimum driver version supporting NVENC SDK 11+ (GeForce driver 456.71+ or newer recommended).
- AMD: Radeon Software Crimson ReLive or Adrenalin drivers supporting AMF 1.4+.
- Intel: Intel Graphics Driver 27.20.100.8000+ supporting OneVPL / QuickSync H.264.

### 5. Frame Rate Baseline & Speed Adaptation
- Default video frame rate is set to **15 fps** (providing silky smooth screen recording while significantly reducing CPU/GPU load).
- If encoding speed drops below `0.95x` for 20 seconds, the dynamic speed watchdog steps down frame rate progressively: 30 → 24 → 15 → 10 → 5 fps (never below 5 fps).
- Settings migration to v2 ensures existing installations upgrade to 15 fps baseline and notify the user with an informational toast.
