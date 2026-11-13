# Contributing

Thanks for helping! Bug reports, ideas and pull requests are welcome.

## Reporting a problem

[Open an issue](../../issues) with your Windows version, display scale, taskbar position and what happened. A screenshot helps a lot.

## Making a change

1. Run `build.cmd` (no SDK or Visual Studio needed) and test `bin\TrayView.exe`. `bin\TrayView-Setup.exe` is the same file; run it to test installing and updating.
2. Keep to the project's goals:
   - **Small and dependency-free.** Use what Windows ships. No NuGet packages, no extra DLLs. Check the exe size before and after.
   - **C# 5**, because the in-box compiler is used (see [docs/technical.md](docs/technical.md)).
   - **Windows 11 look and feel**: Fluent colors, the system accent color, light and dark mode, Segoe UI Variable and Segoe Fluent Icons.
   - **Any display scale**: every size goes through `Px()`, and fonts are sized in pixels. Try 100%, 150% and a scale change while the app runs.
   - **Light at rest**: nothing runs in the background while pages are closed.
3. Describe what you tested in the pull request.

## Code style

Four spaces, braces on their own lines, comments that explain why rather than what. `.editorconfig` has the details.
