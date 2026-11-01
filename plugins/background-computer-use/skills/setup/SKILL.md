---
name: setup
description: Set up Background Computer Use's local Windows native helper and verify background capture/control readiness.
---

# Set up Background Computer Use

Call `background_status` first. This local Windows plugin uses Python 3 and a self-contained native helper. A configured host can run the helper without a global .NET runtime installation or PATH change.

If a helper is already configured, verify its reported identity and recorded executable hash. Otherwise, build the helper from the bundled source using the pinned .NET SDK 10.0.403, then call `configure_backend` with the absolute path to that build. The tool checks the helper and records its executable hash under the plugin's writable data directory. Do not silently configure an arbitrary downloaded executable.

Inspect `native/Build.ps1`, the pinned project, `global.json`, `packages.lock.json` and third-party notices before building. The build script uses `dotnet` from `PATH` or accepts an explicit `-DotnetPath`, and publishes outside the plugin tree. Keep SDK/build caches private; do not change global PATH. WGC requires Windows 10 build 19041 or later; virtual-desktop handback requires a supported Windows 11 22H2+ build. SDK installation and restored dependencies are separate from plugin installation; report what is actually available.

After configuration, verify native status and enumerate windows without activating them. Use the owned test fixture for capture/control acceptance. Confirm occluded pixels, text/button state, foreground and pointer samples, minimized rejection and stale-target rejection. Physical mouse support routes through the existing Computer Use plugin when the user chooses that mode.

If MCP tools are deferred, use the installed plugin's `server.py --list-tools` and `--call configure_backend --args-file PATH` fallback. Arguments are JSON data using the same advertised schemas. No lifecycle hooks or hook trust are needed for this plugin.
