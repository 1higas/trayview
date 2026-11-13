# Technical notes

TrayView is a single C# 5 executable built with the `csc.exe` that ships with the .NET Framework 4.8 inside Windows. It has no dependencies: no NuGet packages, no WebView2 SDK DLLs. The same exe is its own installer.

## Building

```bat
build.cmd
```

This writes `bin\TrayView.exe` and `bin\TrayView-Setup.exe` (the same file under the name that makes it install). The compiler is the in-box one (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, or `FrameworkArm64`), so only C# 5 is available: no `$""`, `?.`, `nameof`, expression-bodied members or auto-property initializers. The exe is AnyCPU and runs natively on x64 and ARM64: .NET Framework 4.8.1 (part of Windows 11) runs AnyCPU apps as ARM64 processes there, and `LoadRuntime` picks the matching `EBWebView\arm64` folder. The in-box compiler can't target ARM64 specifically (only x86, x64, ARM32 and AnyCPU), and there's no need to. CI builds on an ARM64 runner too.

`tools\make-icon.ps1` renders `src\app.ico` (needs the Segoe Fluent Icons font, present on Windows 11).

## Code map (`src/`)

| File | What it does |
| --- | --- |
| `Program.cs` | Entry point, single instance (a named mutex plus a "show" event: starting the exe again opens the running copy's settings, and an "exit" event the setup uses to replace it), version and URLs. `--tray` starts without opening anything (used at sign-in); `--uninstall` removes the app. |
| `Installer.cs` | Per-user setup, chosen by the file name (`TrayView-Setup.exe`): copies the exe to `%LOCALAPPDATA%\Programs\TrayView`, adds a Start menu shortcut and an uninstall entry under `HKCU\...\Uninstall`, and sets Start with Windows. Uninstalling removes all of it, with the settings and browser data. |
| `TrayApp.cs` | The application: one `PageIcon` (tray icon + `SiteView`) per page, the placeholder icon while no page is set up, the onboarding and settings panels, the GitHub update check (latest release, at most daily, when the settings open) and the one-click update: it downloads the release's `TrayView-Setup.exe` to `%TEMP%` and runs it with `--quiet`, which replaces the running copy. |
| `SiteView.cs` | The flyout that shows a page: creates the browser on demand, shows a progress ring until the first navigation completes, frees the browser after the configured delay, resize handles (`Grip`). |
| `WebView.cs` | `Browser`: a minimal WebView2 binding over raw COM vtables (see below). |
| `Flyout.cs` | Base for all flyouts: anchoring next to the tray on any taskbar side, per-monitor DPI, open/close animation, one flyout at a time, close on focus loss or Esc. A centered mode (with a taskbar button) is used for the setup window. |
| `Ui.cs` | `Theme` (Fluent colors from the system theme and accent color) and `FluentPanel`, a hand-drawn Windows 11 UI kit: text, toggles, segmented buttons, buttons, text box, page rows. |
| `Panels.cs` | The onboarding and settings panels. |
| `Settings.cs` | `Site` and `Settings`, stored under `HKCU\Software\TrayView` (pages under `Sites\<id>`), and Start with Windows (`HKCU\...\Run`). |
| `Icons.cs` | Tray icons from favicons, and the globe placeholder drawn like system tray icons. |
| `Native.cs` | P/Invoke: DPI, DWM (rounded corners, border color, dark mode), layered windows, taskbar position, dark menus. |

## WebView2 without the SDK

The WebView2 Runtime is part of Windows 11. Its updater records the install folder in the registry (`SOFTWARE\Microsoft\EdgeUpdate\ClientState\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`, value `EBWebView`, per machine or per user). The runtime's `EBWebView\<arch>\EmbeddedBrowserWebView.dll` exports `CreateWebViewEnvironmentWithOptionsInternal`, the function the SDK's `WebView2Loader.dll` calls. TrayView calls it directly, which removes the need to ship the loader and the managed wrappers (about 900 KB).

This entry point is not documented. It is the same approach as the [webview](https://github.com/webview/webview) library's built-in loader. If a future runtime changes it, `LoadRuntime` is the one place to update (or to fall back to `WebView2Loader.dll`).

Interfaces are called by vtable slot, with the numbers from `WebView2.h` (slots 0-2 are `IUnknown`). Newer interfaces (`ICoreWebView2_15` for favicons, `_19` for the memory target, `Controller2` for the background color) are obtained with `QueryInterface`. The callbacks WebView2 calls are ordinary COM-visible classes implementing one-method interfaces declared with their IIDs.

All pages share one environment, so they share one set of browser processes. `Browser` counts users of the environment and releases it when the last browser closes, so the browser processes exit when no page is loaded.

## Memory

- A page's browser is created when it opens (or when the pointer rests on its icon, or to fetch the favicon of a new page) and is destroyed after the "Free memory after a page closes" delay.
- While hidden but still loaded, the browser is asked to use less memory (`MemoryUsageTargetLevel = Low`).
- After unloading, the app trims its own working set. Idle with no page loaded, TrayView uses about 5 MB.

## Windows and focus

- Flyouts are `WS_POPUP` tool windows with `WS_EX_TOPMOST` set through `CreateParams`. Setting WinForms' `TopMost` instead calls `SetWindowPos` at handle creation, which **activates** the (hidden) window and closes whichever flyout is open.
- The browser's own child window covers the page flyout, so a hit test on the flyout cannot provide resize borders. Instead, three tiny child windows (`Grip`, layered with alpha 1, so invisible but still clickable) sit above it on the two free edges and the free corner, and start the system's resize loop with `WM_NCLBUTTONDOWN`.
- Animations make the window layered for their duration, move it with `SetWindowPos` and step once per DWM frame (`DwmFlush`). The flyout is painted synchronously before the first frame, since the loop holds off `WM_PAINT`. They respect "Animation effects" in Windows settings.
- A page's browser is made visible before the opening animation (`OnOpening`), so the page slides in with its content. Layered alpha works with the browser's child window.
- Only one flyout is open at a time. A flyout that opens activates first and then closes the others: closing the focused flyout first would hand the focus to another app. Focus loss is handled after it settles (`BeginInvoke`), not inside another window's activation.
- Clicking a tray icon doesn't take the focus away from an open flyout (the taskbar doesn't activate), so switching pages goes through the rule above. If the taskbar takes the focus back right after opening, the flyout reactivates.
- A page's context menus and popups belong to the browser process; losing focus to one of them doesn't close the flyout.

## Scaling

Every size comes from one DPI, the effective DPI of the monitor the flyout opens on, through `Px()`. Fonts are created in pixels from it, never in points. `WM_DPICHANGED` rebuilds fonts and re-anchors. Page sizes are stored in device-independent pixels. Tray icons are rendered at the taskbar's DPI and re-rendered on display or theme changes.

## Testing tips

- The Claude Code desktop app runs commands inside an MSIX package: registry writes from processes it starts go to a package-private copy of `HKCU`, and the exe can't be started from some network drives. Test from a local copy, started outside the package when registry state matters.
- New icons start among the hidden tray icons, which open under the flyouts (they're topmost), so switching between pages needs icons pinned to the taskbar. The pin is stored per exe path in `HKCU\Control Panel\NotifyIconSettings\<id>\IsPromoted`.
- To test next to an installed copy, build with another `Program.Id`: it changes the mutex, events, registry key and data folder.
- Synthetic `SetCursorPos` doesn't produce tray mouse-move notifications; use `mouse_event` with relative moves.
