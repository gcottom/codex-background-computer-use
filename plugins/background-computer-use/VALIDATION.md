# Validation — October 30, 2026

Plugin 0.1.2; native backend 1.0.0, Windows x64. The helper SHA256 is `5ce953bb393d7a0ac0513cf2abb0186d5331ff358d349e6c35a2ef8e027318a8`.

## Automated checks

30 behavioral and protocol tests passed: observation replay/expiry, process/window/control identity, geometry changes, controls moving or being replaced under a pixel click, protected/disabled controls, unknown action outcomes, no capture fallback, image integrity and coordinate mapping, per-window serialization, desktop measurements, UTF-8, malformed-request recovery, seven-tool MCP discovery, inline PNG integrity and CLI errors. Both packaged skills passed the skill validator.

```powershell
$env:PYTHONPATH = (Resolve-Path .).Path
py -3 -B -m unittest discover -s tests -v
```

## Real native and Python/MCP acceptance

Tests used an owned WinForms target, completely covered by another owned window. They never moved the physical pointer, activated the target or operated existing user apps. The final core/MCP run recorded **2,201 samples across 12.72 seconds** with foreground HWND/PID, physical cursor position and LastInputInfo unchanged.

- WGC returned the target's distinctive contents while it remained covered: 49,446 target-color pixels, 5,405 counter-color pixels and zero cover-color pixels in the final frame.
- Background text was read back as `python-core-background-verified`.
- Semantic invocation and the image-coordinate button click produced `Counter: 2` through scoped button messages.
- A consumed observation was rejected before another mutation.
- Actual MCP initialize/tools-list/observe produced seven tools and an inline image/png matching the saved image hash.
- The foreground mouse handoff returned metadata with `input_executed: false`; physical mouse injection was not run during these background tests.
- Native tests rejected stale PID, process-start and control identities. Minimized capture returned `window_minimized`, created no PNG and left the target minimized.
- Fixture processes were stopped normally; the final cleanup check found none remaining.

Detailed checkout receipts:

- [Core/MCP summary](assets/validation/core-e2e-summary.json)
- [Native strict-capture and input summary](assets/validation/native-e2e-summary.json)
- [Covered target's captured contents](assets/validation/target-window-covered.png) and [the covering window](assets/validation/cover-window.png)

## Scope

Covered standard Windows controls are verified. Arbitrary apps and accessibility providers can ignore messages or activate themselves; measured interference is reported. Unsupported WGC, protected/non-rendering/minimized targets and secure desktops have no automatic activation/restore fallback. Games requiring raw/foreground input need a separately chosen route. These tests did not inject physical mouse input or change power/display settings.

## Dependency provenance

The private SDK archive matched Microsoft's published SHA512 and had a valid Microsoft Authenticode signature. The restored UIAutomation package passed NuGet author/repository signature verification, matched the lock file, and contained the same WGC assembly bytes as the official release package. The self-contained single-file executable ran without adjacent managed libraries. Source build outputs and caches live outside the plugin package.
