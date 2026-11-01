# Changes

## 0.1.9

- Opt-in handback no longer stalls when keyboard activity occurs during an app-triggered focus change. It returns to the saved window if the cursor is still and the exact action-activated foreground HWND remains active, and reports `user_input_since_observation`.
- Added an immediate foreground-only retry before fresh capture when Windows has restored the desktop but has not yet published the saved foreground HWND. A later retry after capture follows the same exact-foreground and stationary-cursor guards.
- Native helper 1.7.0.

## 0.1.8

- Added `restore_elapsed_ms` to handback results and changed the minimize fallback's fixed wait into foreground polling every 5 ms for up to 50 ms. Native helper 1.6.0.

## 0.1.7

- Added opt-in `minimize_activating_window` fallback to both semantic actions and background clicks. It runs only after guarded foreground restoration fails, the cursor is unchanged, and the current foreground process is verified as belonging to the action app family. It minimizes that exact foreground HWND and checks whether the saved foreground window becomes active.
- Native helper 1.5.0 reports fallback attempts, minimized HWND and final foreground verification separately.

## 0.1.6

- Moved the first opt-in focus-restoration attempt into the native action call so it can respond immediately when an app activates itself.
- Added guarded Shell application-view activation and exact saved-desktop restoration on Windows 11 22H2 and later. The internal virtual-desktop interface is version-sensitive; unsupported or denied operations are reported.
- Reports successful desktop restoration separately from exact foreground-HWND verification for Windows transitions where the visible desktop is restored before the foreground sample catches up.
- Kept a delayed retry after fresh capture only while saved window identity, desktop, cursor and LastInputInfo guards remain unchanged. No keyboard shortcut or physical-input fallback is used.

## 0.1.5

- Added opt-in `restore_foreground` after background UIA/message actions. The helper saves the foreground window identity and virtual desktop, then attempts a guarded handback if the old identity remains valid and no cursor movement or new last-input event occurred.
- Reports successful handback, Windows denial, desktop mismatch, new user input, pointer movement and other skip reasons.
- Native helper 1.3.0 added the guarded restore verb and virtual-desktop identity checks.

## 0.1.4

- Added WGC images for verified visible child-process surfaces, including UIA providers and embedded WebView processes.
- Added identity- and geometry-checked clicks against an exact observed child surface.

## 0.1.3

- Added input routing through visible child HWNDs owned by an app's observed UIA provider process or bounded descendants, including WebView surfaces.
- Added child relationship, process identity, visibility, enabled-state and geometry checks before message delivery.

## 0.1.2

- Updated the packaged integration test to verify the foreground handoff's task-authorization fields.

## 0.1.1

- Revalidate the control under an image-coordinate click, including movement/replacement inside an otherwise unchanged window.
- Preserve task authorization during the optional foreground mouse handoff.

## 0.1.0

- Added strict background WGC capture, scoped control actions and window-message mouse clicks.
- Added expiring single-use observations, process/control/geometry guards, bounded native calls and focus/pointer measurements.
- Added a separate foreground mouse handoff to the existing Computer Use runtime.
- Added seven MCP tools, CLI access, two skills, a pinned self-contained native backend and reusable owned-window tests.
