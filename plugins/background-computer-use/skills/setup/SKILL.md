---
name: setup
description: Set up Background Computer Use's local Windows native helper and verify background capture/control readiness.
---

# Set up Background Computer Use

Call `background_status` first. This local Windows plugin uses Python 3 and a published self-contained native helper. A configured host can run the helper without installing a global .NET runtime or changing PATH.

If a trusted helper build already exists, verify the file and build receipt, then call `configure_backend` with its absolute path. The tool checks the helper and records its executable hash under the plugin's writable data directory. Do not silently configure an arbitrary downloaded executable.

For another host, inspect `native/Build.ps1`, the pinned project, `global.json`, `packages.lock.json` and third-party notices. Build with the declared .NET SDK and publish to a user-selected tool directory. Keep SDK/build caches private; do not change global PATH. The helper uses Windows 10 19041+ WGC and standard Windows UI Automation. SDK installation and restored dependencies are separate from plugin installation; report what is actually available.

After configuration, verify native status and enumerate windows without activating them. Use an owned test fixture for capture/control acceptance. Confirm occluded pixels, text/button state, foreground and pointer samples, minimized rejection and stale-target rejection. Physical mouse support routes through the existing Computer Use plugin when the user chooses that mode.

If MCP tools are deferred, use the installed plugin's `server.py --list-tools` and `--call configure_backend --args-file PATH` fallback. Arguments are JSON data using the same advertised schemas. No lifecycle hooks or hook trust are needed for this plugin.
