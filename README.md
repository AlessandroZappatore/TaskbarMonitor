# Taskbar Monitor

A tiny, free and open-source Windows overlay that shows **live network speed, CPU, RAM, CPU temperature and the currently playing track** right on your taskbar.

- Single ~20 KB executable, no installer, no runtime to install
- Uses ~50 MB of RAM and practically 0% CPU
- No network access, no telemetry, no accounts

![Stats overlay](docs/screenshot-stats.png)
![Now playing overlay](docs/screenshot-media.png)

## Features

| Overlay | What it shows |
| --- | --- |
| **Stats** (bottom right, next to the system tray) | Download / upload speed, CPU %, CPU temperature, RAM % |
| **Now playing** (bottom left) | Cover art, title and artist of whatever is playing (Spotify, YouTube in the browser, VLC, ...). Click it to play/pause |

- Fixed-position columns: values never jump around when they change.
- Values are color-coded from green to yellow to red depending on load and temperature.
- Follows the Windows light/dark theme.
- Sharp text on high-DPI screens.
- The stats overlay can be dragged anywhere; its position is remembered.
- Optional **Start with Windows**.
- Only one instance runs at a time.

## Download

1. Go to the [**Releases**](../../releases/latest) page.
2. Download `TaskbarMonitor.exe`.
3. Put it anywhere you like (for example `C:\Tools\TaskbarMonitor\`) and double-click it.

> **Windows SmartScreen:** the executable is not code-signed, so Windows may show a
> "Windows protected your PC" warning. Click **More info** then **Run anyway**.
> If you prefer, you can read the (single-file) source in [`src/`](src/TaskbarMonitor.cs) and
> [build it yourself](#build-from-source) in a few seconds.

## Usage

Right-click either overlay (or the tray icon next to the clock) to open the menu:

- **Start with Windows**: toggles automatic startup (stored in the current user's `Run` registry key).
- **Show now playing**: shows or hides the now-playing overlay.
- **Reposition on taskbar**: resets the stats overlay to its default place.
- **Exit**: closes the app.

Drag the stats overlay with the left mouse button to move it. Left-click the now-playing overlay to play/pause.

If you move the `.exe` to another folder after enabling *Start with Windows*, toggle the option off and on again so the startup entry points to the new location.

## Requirements

- Windows 10 (version 1809 or newer) or Windows 11, 64-bit
- .NET Framework 4.x (already included in Windows)

## Build from source

No SDK or Visual Studio needed: the compiler (`csc.exe`) ships with Windows.

```bat
git clone https://github.com/AlessandroZappatore/TaskbarMonitor.git
cd TaskbarMonitor
build.bat
```

This produces `TaskbarMonitor.exe` in the repository folder.

## How it works

- **Network speed**: byte counters of all active, non-loopback network interfaces, sampled every second.
- **CPU / RAM**: `GetSystemTimes` and `GlobalMemoryStatusEx` (Win32).
- **CPU temperature**: the *Thermal Zone Information* performance counter (ACPI thermal zones). This is the temperature reported by the system, **not** a per-core reading, so it can differ from tools like HWiNFO or HWMonitor. If your machine does not expose a thermal zone, the value is shown as `--`.
- **Now playing**: the Windows *System Media Transport Controls* (the same API behind the media flyout and keyboard media keys), so any app that integrates with it works.
- **Overlay**: a borderless, always-on-top, non-activating window placed over the taskbar. Windows 11 no longer supports third-party taskbar toolbars, so an overlay is the most reliable approach.

## Privacy

Taskbar Monitor does not connect to the internet, collects nothing, and sends nothing. It only stores two things locally:

- its window position and the now-playing toggle under `HKEY_CURRENT_USER\Software\TaskbarMonitor`
- (optionally) the startup entry under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`

## Uninstall

1. Right-click the overlay and turn **Start with Windows** off, then choose **Exit**.
2. Delete `TaskbarMonitor.exe`.
3. Optionally delete the registry key `HKEY_CURRENT_USER\Software\TaskbarMonitor`.

## Known limitations

- Temperature is the system thermal zone, not an exact per-core value.
- Only the primary monitor is supported.
- The overlays sit on top of the taskbar, so they can cover taskbar items under them (for example the Widgets button on the left). Hide the now-playing overlay from the menu if that bothers you.
- Cover art depends on what the playing app provides.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Released under the [MIT License](LICENSE). Free to use, modify and distribute.
