# Reboot Utility with Online Check

A lightweight Windows system tray application that automatically reboots PCs on a configurable schedule — but only if the machine can reach the network first.

Designed for unattended machines (digital signage, kiosks, point-of-sale systems, office PCs) that need regular reboots to stay healthy without risking a reboot during a network outage.

## Features

- **Scheduled Reboots** — Set a daily reboot time with per-day selection
- **Online Check** — Pings a server before rebooting; skips the reboot if the network is down
- **Ping Retry** — If the first ping fails, waits 2 minutes and tries again before giving up
- **Fuzzy Time** — Optional ±5 minute random offset to stagger reboots across multiple machines and prevent the destination server from interpreting simultaneous pings as a DDoS attack
- **Auto-Update** — Checks for new versions every 24 hours and updates with one click
- **System Tray** — Runs silently in the background with a settings dialog accessible from the tray icon
- **Run at Startup** — Optional Windows startup registration
- **Dark Theme UI** — Modern settings dialog with schedule status indicator

## Screenshot

The settings dialog shows schedule status, day selection, reboot time, ping configuration, and update status in a compact dark-themed window.

## Requirements

- Windows 7 or later
- .NET Framework 4.0

## Installation

### Option 1: Download and run

1. Download `RebootUtility.exe` and `RebootUtilityUpdate.exe` from [`bin/latest/`](bin/latest/)
2. Place both files in a folder (e.g., `C:\Program Files\RebootUtility\`)
3. Run `RebootUtility.exe`
4. Configure your schedule in the settings dialog (right-click the tray icon)

### Option 2: Use the install script

1. Download [`RebootUtility_Install_Update.bat`](src/RebootUtility_Install_Update.bat)
2. Run as Administrator
3. The script downloads the latest binary, installs it, and sets up startup

## Configuration

Right-click the tray icon and select **Settings**:

- **Reboot Time** — When to reboot (24-hour format)
- **Days** — Which days to reboot (or select All)
- **Fuzzy Time** — Add a random ±5 minute offset
- **Ping Server** — Hostname or IP to ping before rebooting (default: `google.com`)
- **Don't reboot if ping fails** — Skip reboot when the server is unreachable

Settings are stored in the Windows registry at `HKCU\SOFTWARE\SystemTrayReboot`.

## Building from Source

Requires the .NET Framework 4 compiler (included with Windows). Run from PowerShell:

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' `
  /target:winexe `
  /out:RebootUtility.exe `
  src/system_tray_reboot.cs `
  /win32icon:src/power.ico `
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:mscorlib.dll
```

**Note:** Must be built with C# 5 (.NET Framework 4 compiler) — no C# 6+ syntax is used.

## Project Structure

```
├── version.txt                         # Current version (used by auto-updater)
├── bin/
│   ├── latest/                         # Always contains the current release
│   │   ├── RebootUtility.exe
│   │   └── RebootUtilityUpdate.exe
│   └── <version>/                      # Archived releases
├── src/
│   ├── system_tray_reboot.cs           # Main application source
│   ├── RebootUtilityUpdate.cs          # Updater helper source
│   ├── RebootUtilityCleanup.cs         # Cleanup utility source
│   ├── RebootUtility_Install_Update.bat# Install/update script
│   ├── Compile.bat                     # Build script
│   ├── CompileUpdate.bat               # Build updater
│   ├── CompileCleanup.bat              # Build cleanup utility
│   └── power.ico                       # Tray icon
```

## How the Auto-Updater Works

1. Every 24 hours, the app downloads `version.txt` from this repo
2. If a newer version is available, "Update Now" appears in the settings dialog
3. Clicking "Update Now" downloads the new exe, validates it (MZ header + version check), and launches `RebootUtilityUpdate.exe` elevated to replace the running binary
4. The updater waits for the main process to exit, copies the new exe into place, cleans up, and relaunches

## License

MIT
