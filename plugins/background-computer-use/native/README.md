# Background Computer Use native helper

Version 1.0.0, first bundled with plugin 0.1.0. Windows x64, per-monitor-v2 DPI aware, self-contained single-file executable. Build with `Build.ps1`; it uses the private .NET 10.0.403 SDK, SHA512-pinned during provisioning, locked NuGet restore, and build artifacts outside the plugin source. See `THIRD_PARTY.md` and `packages.lock.json`.

The executable reads one JSON document from stdin, emits one JSON document to stdout, and exits 0 or 1. Errors are `{ "error": { "code": "...", "message": "..." } }`. The Python host enforces a 12-second outer process timeout because third-party UIA providers can hang. Native WGC waits are limited to 5 seconds, images to 20 megapixels, and UIA traversal to explicit depth/node limits.

## Identity and geometry

Every window request requires `hwnd` (positive decimal string), `pid` (integer), and `start_time` (`Process.StartTime.ToUniversalTime().Ticks`, decimal string). HWND ownership, process creation time, Windows session, and unlocked Default input desktop are revalidated. Actions additionally require exact `expected_title` and `expected_class_name` from a fresh inspection.

Rects use `{ "x": 0, "y": 0, "width": 100, "height": 100 }` in physical screen pixels. UIA bounds use the same keys with double values. Client rect origins are in physical screen coordinates. Runtime IDs are integer arrays and resolve only within the bound HWND's UIA Control View tree.

## Requests

- `{"verb":"status"}` returns backend identity/version, WGC availability, and desktop state.
- `{"verb":"list"}` returns `windows[]` and desktop state. Windows include identity, title, class, minimized/visible state, client rect, and window rect.
- `inspect`: identity plus `depth` (default 6, maximum 32), `max_nodes` (default 300, maximum 5000). Returns `window`, flat `elements[]`, `truncated`, `before`, `after`, and final `desktop`. Elements include runtime identity, bounded name/AutomationId, control type, enabled/offscreen/password flags, physical bounds, supported pattern names, and non-password ValuePattern `value` (maximum 4096 characters) plus `value_truncated`. No password values are read.
- `capture`: identity plus exclusive new absolute PNG `path`, `timeout_ms` (100–5000). Returns path/dimensions, `method:"wgc-only"`, before/after desktop observations and geometry. Minimized targets are rejected. `geometry.screen_rect` exists only when frame dimensions exactly match known DWM/window bounds; `geometry_verified` records this. WGC failure has no PrintWindow, screen-DC, activation, or restore fallback.
- `act`: identity/title/class, `runtime_id`, `expected:{name,automation_id,control_type}`, optional `expected_bounds`, `action` and optional `value`/`direction`. Actions: `invoke`, `set_value`, `toggle`, `select`, `expand`, `collapse`, `scroll` (up/down/left/right). Returns performed/action/pattern/delivery route, runtime ID, and before/after observations. Exact native Edit controls use guarded `WM_SETTEXT`; native Button controls use `BM_CLICK`; other controls use their requested UIA pattern with no ancestor or pattern fallback. Password/disabled/read-only and changed metadata are rejected.
- `click`: identity/title/class, exact `expected_window_rect` and `expected_client_rect`, integer `client_x`/`client_y` within the current client bounds, and `button:"left"|"right"`. It finds a visible/enabled native child inside the bound root (maximum eight levels) and sends window-scoped messages. Left-clicking a native Button uses `BM_CLICK`; otherwise delivery is `WM_MOUSEMOVE`/button down/up. Response reports `messages_delivered`, `target_hwnd`, route, and before/after observations. Message delivery is not proof of semantic success; inspect or capture to verify.

Desktop observations contain foreground HWND, physical cursor coordinates, LastInputInfo tick, process/session desktop names, and an interactive flag. They are observations; changes do not identify whether the user, target app, or another app caused them.

The helper never calls SetFocus, SetForegroundWindow, ShowWindow, SetCursorPos, SendInput, clipboard APIs, or physical input injection. UIA providers and application event handlers can themselves change focus or activate windows in response to an authorized action; the host reports observed changes. Screenshot capture uses the public low-level WGC frame grabber and does not activate or restore windows.

## Validation

Owned-fixture integration tests live in `tmp/background-computer-use/fixture` in the working checkout. They create and terminate their own windows, fully occlude the target, verify text/counter state and distinctive WGC pixels, monitor physical cursor/foreground/input state, and test stale identities and minimized-capture rejection. They do not manipulate existing user applications.

