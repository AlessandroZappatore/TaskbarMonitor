# Contributing

Thanks for your interest in Taskbar Monitor! Bug reports, ideas and pull requests are welcome.

## Reporting a bug

Open an issue and include:

- Windows version (Win + R, then `winver`) and display scaling
- What you expected and what happened (a screenshot helps)
- Whether you have more than one monitor

## Building and testing

```bat
build.bat
TaskbarMonitor.exe
```

Close any running instance first (right-click the overlay, then **Exit**), otherwise the build cannot overwrite the `.exe`.

Before opening a pull request run the smoke test. It builds a copy of the sources, starts it, opens the Settings window and checks the main paths (damaged settings, saving a value, category headings, swapping duplicates, the Close button):

```bat
powershell -ExecutionPolicy Bypass -File tests\smoke.ps1
```

Close Taskbar Monitor first. The test saves and restores your settings and does not touch your `TaskbarMonitor.exe`.

## Pull requests

- Keep the project lightweight: no external dependencies, no network access, single source file where reasonable.
- Match the existing code style.
- Describe what you changed and how you tested it.
- By contributing you agree that your contribution is released under the [MIT License](LICENSE).
