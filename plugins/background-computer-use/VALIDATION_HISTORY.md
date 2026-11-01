# Validation history

> Historical receipts and iteration notes for plugin 0.1.3–0.1.9. The current, release-focused evidence summary is in [VALIDATION.md](VALIDATION.md). Historical measurements describe the builds noted in each section and are not current performance guarantees.

## 0.1.9 typing-tolerant guarded handback — November 1, 2026

- `restore_foreground` now continues through keyboard activity when the cursor remains still and the exact window activated by the action is still foreground. It reports the changed LastInputInfo sample. Pointer movement or another foreground HWND still stops restoration. The delayed retry after capture follows the same exact-window guard.
- The user's rerun showed why this matters: ChatGPT was the saved foreground; the Home selection moved focus to VirtualBox while the user typed, and the old helper returned `user_input_since_observation` without handback. The next live check uses the saved ChatGPT HWND with the new opt-in semantics.
- Python/core/server suite: 39 tests passed, including keyboard activity allowed with a stationary cursor and rejection when the foreground HWND changes.
- Native helper 1.7.0 built to `D:\Tools\BackgroundComputerUse\native-staging-0.1.9`; SHA-256 for the release-checkout build: `7C30EF6D42D95D11584835B38C150995BA0B985BD19F5EBA20717E463E956A2C`. Assembly version metadata matches helper version 1.7.0.
- Live end-to-end Home selection through the isolated plugin core: the first native handback restored the saved desktop in 61 ms but Windows had not yet published ChatGPT as foreground. The core immediately issued a foreground-only retry before taking the fresh screenshot; that verified ChatGPT in 58 ms. Total measured native handback work was about 119 ms, and the returned focus summary verified the exact ChatGPT HWND. LastInputInfo did not change during this run; the typing case is covered by the synthetic regression test.
- The helper remains staged and was not deployed or configured. Plugin-data still points to 0.1.6.

## 0.1.8 handback latency experiment — November 1, 2026

- The helper now tries the existing Shell/foreground routes in a different order, allows the virtual-desktop foreground transition to settle for at most 150 ms, and reports `restore_elapsed_ms`. The opt-in minimize fallback now polls foreground every 5 ms for up to 50 ms instead of always sleeping 75 ms.
- Python/core/server suite: 37 tests passed. Native helper 1.6.0 compiled to the isolated staging folder `D:\Tools\BackgroundComputerUse\native-staging-0.1.8`; final SHA-256: `54B2E5EE063477F467A5639C6D2F19B847C5FDDAA5533F3543BDB8DDB334A4C7`.
- Live timing experiments with earlier 0.1.8 activation ordering returned in 62–69 ms but restored the desktop without verifying the exact foreground HWND. A later run took 189 ms and also left foreground verification pending. These results show that faster calls alone do not guarantee a faster completed handback.
- Per the user's request, the Home selection was rerun after another agent opened Vortex. That action advanced `LastInputInfo` during the UIA action, so the guard returned `user_input_since_observation` and skipped handback. This does not validate the final 150 ms transition-wait build. The staged helper remains unconfigured; the plugin-data helper remains on 0.1.6.

## 0.1.7 guarded minimize fallback and VirtualBox Home check — November 1, 2026

- Added the opt-in `minimize_activating_window` argument to semantic actions and background clicks. It requires `restore_foreground`; the native helper checks the cursor and last-input baseline again immediately before minimizing, confirms the foreground process is in the action app family, minimizes only that current foreground HWND, and reports whether that HWND became minimized and whether the previous foreground HWND was verified.
- Python/core/server suite: 37 tests passed, including option validation and propagation to both the in-action and delayed native restore request.
- Native helper 1.5.0 compiled and published to the isolated staging folder `D:\Tools\BackgroundComputerUse\native-staging-0.1.7`; final SHA-256: `3A3AE6A41B4A45D339AFB2EC0EC371004F62BBE49929DF4C9E40194061B9AD76`. Status reports 1.5.0 and WGC support.
- Live VirtualBox check: selected the Manager's **Home** item through UI Automation while the Manager was behind ChatGPT. This did not open or spawn a VM console, but the selection itself brought the existing Manager to the foreground and changed to its virtual desktop. The guarded handback switched back to the saved desktop and verified ChatGPT's exact foreground HWND; cursor and last-input samples stayed unchanged during the restore. This shows the focus issue is not exclusive to console spawning: background UIA on an already-open Manager can also activate it.
- The live Home check restored normally, so the new minimize fallback was not invoked live. Its behavior is covered by opt-in wiring tests and native compilation; a live end-to-end fallback remains unverified. The staged helper was not deployed or selected in plugin-data configuration; that still points to the 0.1.6 helper.

> The October 30, 2026 results below are historical validation for plugin 0.1.2 and native helper 1.0.0. The November 1, 2026 local build and deployment results are recorded in the 0.1.3 section.

## 0.1.6 local build and guarded handback — November 1, 2026

- Python/core/server suite: 36 tests passed, including assertions that the native in-action restore runs before fresh capture and that successful desktop restoration remains distinct from a lagging foreground-HWND sample.
- Native helper 1.4.0 compiled successfully. Final staged SHA-256: `B59F03EAD1EE4040303E743174E3E75ECBC187BD720EB6B885684F22C830F178`.
- Python/core/MCP owned-window fixture on the final helper: `passed: true`, including covered capture, value readback, two background clicks, seven tools, consumed-observation rejection and a child-surface image. Foreground, cursor and LastInputInfo remained unchanged for the full 20-second run.
- Native owned-window fixture on the final helper: `passed: true`; covered-window pixels were captured, UIA value update and two message clicks worked, identity rejections were exercised, and foreground/cursor remained unchanged with no external input observed.
- Live VirtualBox testing showed the GPTVM window with a background “Show” action. The taskbar had a pinned ChatGPT icon, but a verified background click delivered messages without changing foreground or desktop. The guarded Shell desktop-switch route then restored the saved desktop; `SetForegroundWindow` reported denial and the immediate HWND sample lagged. The user confirmed that the visible desktop returned to the screen state from before the VM switch. The result schema now reports `desktop_restored` separately from `foreground_verified`. The final build also adds Shell application-view activation after the desktop switch; that refinement compiled and passed unit tests but was not rerun live because user input resumed.
- Source CLI is configured to `D:\Tools\BackgroundComputerUse\native-staging-0.1.6\BackgroundComputerUse.Native.exe`, with the hash above. The active Codex plugin cache is still 0.1.2, whose MCP schema does not expose `restore_foreground`; the source CLI supports it now.

Receipts are under `D:\Tools\BackgroundComputerUse\validation-native-0.1.6-final2\f114b0d6c306487699286587928f0f7f` and `D:\Tools\BackgroundComputerUse\validation-mcp-0.1.6-final3\b71ea9e694754a0b9ded051faab5f35c`.

## 0.1.3 local build, deployment, and verification — November 1, 2026

- Locked native restore/publish succeeded. Native helper 1.1.0 SHA-256: `51C55B63F8A40C5B98E96589D1CD8FE646D04D8E15DACD36F0A44590C8030BD7`.
- Python/core suite: 30 tests passed.
- Native owned-window integration: WGC captured the covered target and excluded cover pixels; set-value readback matched; semantic invocation and image-coordinate click advanced the counter to 2; stale PID/start/runtime identities were rejected; minimized capture stayed minimized. The run's overall preservation result was inconclusive because the physical pointer and LastInputInfo changed during sampling; foreground stayed unchanged.
- Python/core/MCP owned-window integration: functional assertions passed, including text/counter readback, consumed-observation rejection, image/hash integrity, seven MCP tools and non-input foreground-handoff metadata. The harness's overall preservation result was inconclusive because the cursor and LastInputInfo changed; foreground stayed unchanged.
- The helper was deployed to `D:\Tools\BackgroundComputerUse\native\BackgroundComputerUse.Native.exe`, hash-checked, and configured in plugin data. The previous helper remains at `D:\Tools\BackgroundComputerUse\backups\BackgroundComputerUse.Native-1.0.0-5CE953BB393D7A0AC0513CF2ABB0186D5331FF358D349E6C35A2EF8E027318A8.exe`.
- The active backend reports native helper 1.1.0 with WGC support. The current Codex session still reports plugin server 0.1.2; refreshing its cache failed with Windows sharing violation (error 32) because the active session has the cache in use. The local marketplace source is 0.1.3; reinstall it after Codex releases the active cache.
- Armoury Crate/WebView2 interaction is not yet verified. At the follow-up capture, its ApplicationFrame window was minimized and its remaining CoreWindow frame was blank. No application click was sent. Pointer/input activity was also changing, so no test action was sent into the live app.

Fixture receipts are under `D:\Tools\BackgroundComputerUse\validation-native-0.1.3` and `D:\Tools\BackgroundComputerUse\validation-mcp-0.1.3`.

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
