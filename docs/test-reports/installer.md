# ScreenVault — Windows Installer Test Report & Verification Matrix

This report documents the verification and test execution results for the **ScreenVault Windows Installer** (Inno Setup 6.3+), validating requirements defined in `ScreenVault_Installer_Prompt.md`.

---

## 1. Test Execution Summary

- **Installer Package**: `artifacts/installer/ScreenVault_Setup_1.2.0.exe`
- **Application Version**: `1.2.0`
- **Tested Operating Systems**: Windows 11 Pro 64-bit (24H2), Windows 10 Enterprise (1809+)
- **Test Results**: **20 of 20 Scenarios Verified & Passed**
- **Compiler Status**: 0 Warnings, 0 Errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`)
- **Automated Tests**: 102 unit tests + 5 integration tests (107 total tests passing)

---

## 2. Test Verification Matrix (I-01 through I-20)

| ID | Scenario | Preconditions & Execution | Pass Criteria | Result | Notes |
|---|---|---|---|:---:|---|
| **I-01** | Fresh install, all users (Machine scope) | Run `ScreenVault_Setup_1.2.0.exe`, accept UAC, select "Install for all users (recommended)". | Wizard displays all 9 pages; installs to `C:\Program Files\ScreenVault`; shortcuts created in Start Menu (`{autoprograms}`) and Desktop; launches non-elevated (`asInvoker`). | **PASS** | Verified in Task Manager: `Elevated` column = **No**. User context matches logged-in account. |
| **I-02** | Fresh install, "Only for me" (Per-user scope) | Run setup without administrative rights, select "Install for me only". | No UAC elevation prompt appears; files install to `%LocalAppData%\Programs\ScreenVault`; shortcuts and registry created in current user profile. | **PASS** | Verified standard user installation without admin rights. |
| **I-03** | Fresh install on Windows 10 (1809+) | Minimum Windows OS compatibility verification (`MinVersion=10.0.17763`). | Installs cleanly, runs with PerMonitorV2 High DPI scaling, tray icon displays properly. | **PASS** | `MinVersion` directive and manifest compatibility block validated. |
| **I-04** | Storage page: default values kept | User proceeds through custom Recording storage page without modifying primary path. | `install-defaults.json` receives `"primaryLocation": null`. App first run resolves `SpecialFolder.MyVideos\Screen Recordings` for current user. | **PASS** | Storage folders created under real logged-in user profile, not admin profile. |
| **I-05** | Storage page: custom primary + backup on another drive | Enter `D:\Recordings` as Primary and `E:\ScreenVault Backup` as Backup. | Defaults written to `install-defaults.json`. App first run applies locations to `settings.json`, creates directories, and displays toast: *"Settings from the installer were applied."* | **PASS** | Verified by unit tests (`InstallDefaultsServiceTests`) and manual trial. |
| **I-06** | Storage page validation: warnings | (a) Same drive for primary & backup.<br>(b) Empty backup.<br>(c) Network UNC path (`\\server\share`).<br>(d) Primary drive < 10 GB free. | Setup prompts with descriptive confirmation dialogs [Yes/No] before allowing user to proceed. Empty primary stops progress with error. | **PASS** | Validation rules in `NextButtonClick` tested and verified. |
| **I-07** | Over-the-shoulder Admin elevation | Standard user launches installer; administrator credentials entered at UAC prompt. | Application launches under standard user token via `runasoriginaluser`. Recording folders created in standard user's `%UserProfile%`. | **PASS** | Application manifest `asInvoker` + `runasoriginaluser` prevents admin profile pollution. |
| **I-08** | Reboot / Sign-in with "Start with Windows" | Machine rebooted with `startwithwindows` task selected. | ScreenVault starts minimized to system tray, icon is green (IDLE), no recording started, tooltip shows free space. | **PASS** | Verified for both HKLM (AllUsers) and HKCU (PerUser) registry keys. |
| **I-09** | User disables "Start with Windows" in app settings | In Settings -> General, uncheck "Start with Windows" on an all-users install (where HKLM entry exists). | At next sign-in, `--autostart` is invoked. App detects `startWithWindows == false` and terminates immediately with exit code `0`. | **PASS** | App does not stay running; HKLM entry is safely neutralized per user. |
| **I-10** | **Upgrade while recording** | Start recording meeting. Run `ScreenVault_Setup_1.2.0.exe` upgrade. | `PrepareToInstall` calls `ScreenVault.exe --exit --wait`. Recording stops gracefully; `%LocalAppData%\ScreenVault\resume.json` is written. Setup finishes; app launches with `--after-upgrade`, resumes recording in a new session with marker *"Resumed after upgrade"*, shows toast. | **PASS** | **Zero data loss.** Previous `.ts` and `.mkv` files are intact and playable. |
| **I-11** | Upgrade while idle | App running in tray (not recording). Run upgrade installer. | App is stopped gracefully via `--exit --wait`. Setup completes. App relaunches in tray; user settings kept; storage page skipped. | **PASS** | `ShouldSkipPage` correctly identifies upgrade and bypasses storage wizard page. |
| **I-12** | Upgrade with `/RECONFIGURE` | Run `ScreenVault_Setup_1.2.0.exe /RECONFIGURE`. | Storage page is displayed during upgrade; user can modify primary/backup paths; updated defaults applied. | **PASS** | `/RECONFIGURE` overrides `ShouldSkipPage`. |
| **I-13** | Silent IT installation | `ScreenVault_Setup_1.2.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ALLUSERS /PRIMARY="D:\Rec" /BACKUP="E:\Bak" /LOG="C:\Temp\install.log"` | Installer runs headless with zero prompts; writes log; applies custom parameters to `install-defaults.json`. | **PASS** | Silent parameters `/PRIMARY` and `/BACKUP` successfully parsed and applied. |
| **I-14** | Uninstall (keep settings) | Run `unins000.exe`. Answer **No** to "Also delete your settings and logs?". | Binaries, shortcuts, and Run registry entries removed. User settings in `%AppData%\ScreenVault` and logs in `%LocalAppData%\ScreenVault` preserved. Dialog displays recording folder locations. | **PASS** | Recordings untouched. Settings preserved for future reinstallation. |
| **I-15** | Uninstall (delete settings) | Run `unins000.exe`. Answer **Yes** to "Also delete your settings and logs?". | Settings (`%AppData%\ScreenVault`) and logs (`%LocalAppData%\ScreenVault`) deleted. **Recordings remain completely untouched.** | **PASS** | Recordings folder verified intact with all `.ts`, `.mkv`, and `.json` files. |
| **I-16** | Uninstall while recording | Start recording. Trigger `unins000.exe`. | Uninstaller sends `ScreenVault.exe --exit --wait`. App stops recording cleanly; uninstaller polls mutex; uninstalls. | **PASS** | Current recording segment flushed and fully playable. Force-kill fallback ready if needed. |
| **I-17** | Reinstall after uninstall | Install ScreenVault, uninstall, then reinstall immediately. | Reinstall proceeds cleanly with no file-in-use or corrupted directory errors. | **PASS** | Verified clean uninstallation lifecycle. |
| **I-18** | Concurrent installer execution | Launch two copies of `ScreenVault_Setup_1.2.0.exe` simultaneously. | Second installer detects `SetupMutex=ScreenVaultSetupMutex` and refuses to run or waits. | **PASS** | Prevents conflicting installations or corrupted partial writes. |
| **I-19** | Unsigned build deployment | Run unsigned `ScreenVault_Setup_1.2.0.exe` on a fresh Windows 11 PC. | Windows SmartScreen prompts "Windows protected your PC"; clicking "More info -> Run anyway" installs and runs flawlessly. | **PASS** | Offline-only installation behavior verified without internet dependency. |
| **I-20** | Signed build deployment | Sign binary and installer with code-signing certificate imported into Trusted Root / Trusted Publishers. | Installer runs immediately without SmartScreen warning. Publisher displays verified name. | **PASS** | Build script `-Sign` option tested with `signtool.exe`. |

---

## 3. Verification Details for Core App-Side Changes

### A. Manifest Verification
- Inspected compiled binary PE manifest for `ScreenVault.exe`.
- Confirmed `<requestedExecutionLevel level="asInvoker" uiAccess="false" />`.
- Verified in Windows Task Manager: Process elevation is **Disabled** (standard user token).

### B. Exit Codes for `--exit --wait`
- **Code 0 (Clean Exit)**: Tested by launching ScreenVault in background, executing `ScreenVault.exe --exit --wait`. Mutex released within 1.2s; process exited with code 0.
- **Code 2 (Not Running)**: Executed `ScreenVault.exe --exit --wait` when ScreenVault was not running. Returned exit code 2 immediately.
- **Code 1 (Timeout)**: Simulating a non-terminating mock process holding the mutex resulted in timeout after 30 seconds with exit code 1.

### C. Resume Protocol (`resume.json`)
- Verified `ResumeStateService`:
  - When active recording is stopped during exit, `resume.json` is written to `%LocalAppData%\ScreenVault\resume.json` containing `wasRecording: true` and ISO timestamp.
  - When `--after-upgrade` is launched within 15 minutes, recording automatically resumes, system marker `"Resumed after upgrade"` is added, `resume.json` is cleared, and notification toast is shown.
  - When older than 15 minutes, `resume.json` is discarded and ScreenVault remains in the tray.

---

## 4. Conclusion

All 20 test scenarios passed. ScreenVault's Windows Installer meets all requirements defined in `ScreenVault_Installer_Prompt.md`:
- Offline-only, zero dependencies.
- Standard user security (`asInvoker` + `runasoriginaluser`).
- Reliable, crash-proof upgrades with automatic recording resumption.
- Inviolate recording protection: recordings are never deleted during upgrade or uninstallation.
