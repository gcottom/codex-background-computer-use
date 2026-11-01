# Background Computer Use

A local Windows companion to Computer Use. It views covered windows without activating them and operates controls without moving the user's physical pointer. Physical mouse control remains available as an explicitly chosen foreground route through the existing Computer Use runtime.

## What it provides

- Strict WGC-only window snapshots, including covered windows. Capture never restores, activates or moves the target and has no foreground/screen fallback.
- Current accessibility controls with HWND/PID/process-start bindings, expiring single-use action observations and bounded provider calls.
- Background text/button/control actions and image-coordinate window-message clicks. App/provider effects and foreground/pointer samples are reported.
- A validated handoff to the normal foreground mouse runtime when the task calls for it.
- Seven MCP tools plus a deterministic CLI route when chat tools are deferred.

Apps can ignore background messages or activate themselves from a control handler. Minimized/protected/non-rendering windows can be unavailable. The plugin reports these limits instead of taking the mouse or focus automatically. See the bundled skill for the operation loop.

## Setup

The host build is published outside the package at `D:\Tools\BackgroundComputerUse\native\BackgroundComputerUse.Native.exe`. Python 3 is required; the published helper is self-contained. Configure it through `configure_backend`. Configuration and captures default to `~/.codex/plugin-data/background-computer-use`; `BCU_DATA_DIR` selects an isolated test root.

For another machine, build the pinned `native` project using `native/Build.ps1`, then configure the resulting trusted executable. Read `native/THIRD_PARTY.md` and the build/toolchain receipts. No global PATH or power/display settings are changed.

```powershell
py -3 "$pluginRoot\server.py" --list-tools
py -3 "$pluginRoot\server.py" --call background_status
py -3 "$pluginRoot\server.py" --call observe_background_window --args-file $requestJson
```

The JSON request uses the same MCP input schema. Take one action from a fresh observation, then inspect the returned fresh observation before continuing. Screenshots can be returned inline by MCP or viewed from their absolute saved paths with the host's image tool.

## Validation

See `VALIDATION.md` for the actual tested build, fixture results and remaining scope. This plugin has no lifecycle hooks and requires no hook trust changes.
