# Changelog

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
