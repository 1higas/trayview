# TrayView

A Windows tray app that pins web pages to the notification area: one icon per page (its favicon), and clicking it opens the page in a small flyout hosted by WebView2. Public repo (planned): https://github.com/1higas/trayview. Sibling project with the same approach and UI kit: https://github.com/1higas/snapcap.

## Goals and preferences (from the owner)

- **Minimal everything**: smallest exe, least code, lowest CPU and RAM. Use what Windows ships; no NuGet packages or extra DLLs. The exe is ~90 KB.
- **Windows 11 look**: Fluent colors, system accent color, light/dark mode, Segoe UI Variable, Segoe Fluent Icons, DWM rounded corners. The UI kit is hand-drawn (`src/Ui.cs`, shared with snapcap).
- **Scale-agnostic**: every size through `Px()` from the monitor's effective DPI; fonts in pixels; page sizes stored in DIPs.
- **Elegant animations**: flyouts slide in from the taskbar's side and fade; respect the system "Animation effects" setting.
- **Taskbar on any side** (Windows 11 allows it): flyouts anchor to the tray corner; pages resize from the opposite edges/corner.
- **Light at rest**: the browser is created on demand (open, hover over the icon, favicon fetch for a new page) and destroyed after the "Free memory" delay. Cold starts show a progress ring until the first navigation completes.
- Everything in English (UI, code, comments). README is for end users; technical detail goes to `docs/technical.md`.
- The owner prefers to review before anything is committed or pushed; ask first.

## Build and test

- `build.cmd` → `bin\TrayView.exe` and `bin\TrayView-Setup.exe` (same file; "setup" in the name makes it a per-user installer, no portable release) with the in-box `csc.exe` (C# 5 only: no `$""`, `?.`, `nameof`, expression-bodied members, auto-property initializers).
- A running TrayView locks the exe: `Stop-Process -Name TrayView` before building.
- The repo lives on a network drive (`T:`); the exe can't be started from there inside Claude Code's shell. Copy it to a local folder to run it.
- Claude Code's desktop app is an MSIX package: processes started from its shells write `HKCU` to a package-private hive. Registry state seen from the shell is that private copy.
- UI testing: drive the mouse with `SetCursorPos`/`mouse_event` from a per-monitor-DPI-aware script and capture with `CopyFromScreen`. Tray hover needs real (relative) `mouse_event` moves. New icons appear among the hidden tray icons.
- Test machine: two 3840x2160 monitors, 100% (primary) and 125%.
- The owner often has their own TrayView running. Test with a copy built with `Program.Id = "TrayViewTest"` (separate mutex, events, settings and data) and never stop or click their instance.
- ARM64: the AnyCPU exe runs natively; the in-box csc has no ARM64 target. CI also builds on `windows-11-arm`.

## Gotchas

- Don't set `Form.TopMost`; use `WS_EX_TOPMOST` in `CreateParams`. `TopMost` activates the window at handle creation and closes open flyouts.
- The animation loop blocks `WM_PAINT`: call `Refresh()` after `Show()` before animating.
- Show a page's browser in `OnOpening` (before the animation), or the flyout animates as an empty gray box.
- Opening a flyout must activate it before dismissing the others; dismiss on focus loss via `BeginInvoke`. Otherwise switching pages from the taskbar opens and immediately closes the new one.
- `Control.Site` exists; the page model class is `TrayView.Site` (qualify it inside controls).
- WebView2 vtable slots come from `WebView2.h` of the SDK NuGet package (index = position in the `...Vtbl` struct, IUnknown included).
