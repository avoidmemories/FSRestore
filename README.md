# FSRestore

Restores fullscreen apps (browsers, Discord, etc.) after a game with a custom resolution minimizes. Fixes the "window is technically maximized but visually shrunk" bug that happens when a stretched-resolution game alt-tabs out.
https://youtu.be/ITtm5PsTJ34

<p align="center">
  <a href="https://youtu.be/ITtm5PsTJ34">
    <img src="https://i.imgur.com/h8x2yUi.png" alt="Preview" width="600">
  </a>
</p>

## How it works

- You pick **games** (triggers) and **apps** (targets) in the UI.
- When a tracked game loses foreground or minimizes, the guardian snaps every target window back to its monitor's work area via `SetWindowPos` — no `SW_MAXIMIZE` animation, no focus stealing.
- Handles Alt+Tab, Win+D (leaves the desktop clean), in-game minimize buttons, and slow restores via a 4-second armed repair window.
- Remembers which apps were fullscreen before, so it only restores the ones that actually need it.
- Runs in the system tray; optionally starts with Windows.

## Tech

.NET 8, WPF, Win32 P/Invoke (`SetWinEventHook`, `SetWindowPos`, `MonitorFromWindow`, `GetMonitorInfo`, `QueryFullProcessImageName`, `ExtractIconEx`, `DwmSetWindowAttribute`). No third-party UI libraries.

## Build

Requires .NET 8 SDK.
