# Background Computer Use native helper

Native helper **1.7.0**, bundled with plugin **0.1.9**. This is a self-contained, single-file Windows x64 application built for per-monitor DPI awareness. The helper reads one JSON request from stdin, writes one JSON response to stdout, and exits. Errors use `{ "error": { "code": "...", "message": "..." } }`.

The project targets Windows 10 build 19041 or later for Windows Graphics Capture. Desktop restoration uses internal, version-sensitive Windows Shell interfaces and requires a supported Windows 11 build (22H2 or later). The helper uses Windows UI Automation and Microsoft's WinApp UI Automation WGC provider.

## Build

The project pins .NET SDK 10.0.403 in `global.json` and uses the locked package graph in `packages.lock.json`. On Windows, run `Build.ps1`; it locates `dotnet` on `PATH` by default and publishes outside the plugin source tree under `%LOCALAPPDATA%\BackgroundComputerUse`.

```powershell
Set-Location .\plugins\background-computer-use
.\native\Build.ps1
```

To use an SDK installed outside `PATH`, pass `-DotnetPath`. `-OutputPath` and `-BuildRoot` can also select other absolute paths. The build verifies the package lock and prints the resulting executable's SHA-256. See [THIRD_PARTY.md](THIRD_PARTY.md) for dependency and SDK provenance.

## Window identity and geometry

Every window request is bound to a positive decimal HWND, process ID, and process start time. The helper rechecks HWND ownership, process creation time, Windows session, and the unlocked Default input desktop. Actions also validate the observed title, class, control identity and, where applicable, the expected bounds.

Physical rects use `{ "x": 0, "y": 0, "width": 100, "height": 100 }`; UI Automation bounds use the same keys with decimal values. Image-coordinate actions are accepted only when WGC frame dimensions match the verified screen mapping and the target is inside client content. Child-surface actions remain bound to an observed provider process, HWND, and geometry.

## Request routes

- `status` reports helper identity/version, WGC availability and desktop state.
- `list` enumerates visible same-session windows and their identity, bounds and minimized state.
- `inspect` returns bounded UI Automation controls and verified child-process surfaces. Traversal is limited to depth 32 and 5,000 nodes. Password values are never read; protected, disabled, read-only, stale, or moved controls are rejected.
- `capture` writes a new PNG using WGC only. It rejects minimized root windows and has no PrintWindow, screen-DC, activation, or restore fallback. Images are limited to 20 megapixels and frame acquisition to five seconds.
- `act` performs one supported UIA pattern action. Exact native edit and button controls use guarded `WM_SETTEXT` or `BM_CLICK`; other controls use the requested pattern without ancestor or input fallback.
- `click` sends window-scoped mouse messages to the exact observed root or child surface. It does not move the global pointer or synthesize global mouse input. Delivery is not proof of semantic success.
- `restore_foreground` is used only when the caller explicitly opts into `restore_foreground`. It validates the saved foreground identity, desktop, cursor, current action-activated HWND, and user-input observations before attempting the handback.

## Foreground handback and minimizing

An app may activate itself while responding to an otherwise background UIA or window-message action. With `restore_foreground` enabled, the helper can return to the exact saved foreground window and, on supported Windows 11 builds, restore its saved virtual desktop. Windows may complete that desktop transition before it publishes the foreground HWND; the plugin therefore makes a second foreground-only restore call immediately after the first native action and before fresh capture when needed.

Keyboard activity does not cancel this explicitly requested return while the pointer remains still and the exact action-activated HWND remains foreground. The result exposes `user_input_since_observation` and `restore_elapsed_ms`. Pointer movement, an unexpected foreground HWND, changed window identity, an unexpected process/input desktop, or unsupported Shell behavior is reported and can stop the action.

The caller can separately set `minimize_activating_window: true`. If restoration still fails, the helper minimizes only the current foreground HWND after checking that its process belongs to the same executable family as the app that received the action. It reports the HWND and whether Windows actually minimized it. The app remains running. This option changes the target window state and should be used only when that behavior is acceptable.

The virtual-desktop and application-view COM interfaces are internal and version-sensitive Windows interfaces, not stable public contracts. A desktop restoration result is reported separately from exact foreground-HWND verification. No keyboard shortcuts, global `SendInput`, clipboard operations, or `SetCursorPos` are used.

## Validation

The plugin's owned-window fixture and Python/core/server tests are under `tests/`. The fixture creates and terminates only its own windows. It covers occluded WGC capture, control readback, scoped message delivery, stale identity rejection, minimized-window behavior, and desktop/pointer observation. Current release evidence is in [VALIDATION.md](../VALIDATION.md); previous release receipts are in [VALIDATION_HISTORY.md](../VALIDATION_HISTORY.md).
