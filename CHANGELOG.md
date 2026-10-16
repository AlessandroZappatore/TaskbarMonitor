# Changelog

## 1.3.1

- Fix: the download and upload speed no longer drops to 0 for one second every 10 seconds during a steady transfer.
- Fix: network speed is no longer doubled when Tailscale, WSL or other virtual adapters carry traffic: only adapters with a default gateway are counted.
- Fix: the Wi-Fi value recovers after the Windows WLAN service restarts instead of showing `--` until the app is restarted.
- Fix: a stuck media player can no longer freeze the now-playing widget (calls to Windows now time out after 3 seconds).
- Fix: a damaged value in the settings (text instead of a number, an unknown value in the list) no longer stops the app from starting.
- If the hotkey Ctrl+Alt+M is already used by another program, the app says so at startup.
- The widgets no longer take measurements while they are hidden, and the two widgets no longer raise each other every second.
- The first read of the battery cycle count no longer delays the widget at startup (about 1 s), and only system DLLs are loaded.
- New `tests/smoke.ps1` smoke test; about 40 lines of unused code removed.

## 1.3.0

- **Six more values to choose from**: CPU power in watts (Intel RAPL), CPU frequency, CPU thermal limit, Wi-Fi signal, GPU memory in use and battery cycle count. They are only measured when selected and add no noticeable load (about 0.1% of one core for the process with all six active).
- The Settings drop-down groups the 19 values by category (Network, CPU, GPU, Memory, Disk, Battery and system), with non-selectable headings and indented items. Clicking a heading selects its first item; the arrow keys skip headings.
- The executable is now about 44 KB.
- CPU temperature is now the average of the last 5 seconds instead of the instantaneous sensor sample, which on many laptops jumps by tens of degrees from one second to the next.

## 1.2.0

- **Choose what to show.** New *Settings...* window: the stats widget has six positions (3 columns of 2) and each one can show download speed, upload speed, CPU usage, CPU temperature, RAM usage or used GB, disk activity, disk read speed, disk write speed, disk free space, GPU usage, battery or PC uptime. Positions can be left empty, empty columns disappear and the widget gets narrower. Labels and units change with the selected value, and only the selected values are measured.
- More compact widget: column widths are measured from the text, so labels and numbers sit closer together and the widget takes less space.
- The widget position is stored as its right edge, so the widget can grow and shrink without covering the notification area.
- Fix: the **Close** button of the Settings window now closes it.
- New `--settings` start option; the executable is now about 37 KB.

## 1.1.0

- Hide and show the widgets: menu, click on the tray icon, `Ctrl+Alt+M`, or open the app again while it is running.
- Menu labels are in English.

## 1.0.1

- Fix: the widgets stay visible when the Start menu or other flyouts open, without flicker (they are owned by the taskbar window).

## 1.0.0

- First release: live network speed, CPU, RAM and CPU temperature widget, now-playing widget with cover art, color-coded values, light/dark theme, high-DPI support and optional start with Windows.
