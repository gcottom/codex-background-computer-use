# Opt-in owned Windows test fixture

These scripts open two fixture-owned windows only when explicitly run. Importing Python modules, installing the plugin, and normal unit-test discovery do not launch them. No dependency, helper executable, or runtime is installed by these scripts. Run on the same interactive Windows session as the native helper. Windows PowerShell 5.1 compiles the C# fixture from .NET Framework system assemblies; the Python harness uses only the standard library.

The target has an Edit/TextBox named **Test value** (control ID 1001), an **Increment** button (1002), and a **Counter** label (1003). A fully opaque owned window covers the target's entire outer rectangle. Both forms use `ShowWithoutActivation`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`, and `MA_NOACTIVATE`. They appear near the primary display's lower-right corner without taskbar entries. The hidden launcher uses `CreateProcessW`, `CREATE_NO_WINDOW`, `SW_HIDE`, and no inherited handles. The source has no foreground activation, focus-setting, global input injection, or pointer movement calls.

Run the commands below from the **plugin root**, containing `server.py` and `bcu/`. Use an existing console. Each complete test closes its own fixture in `finally`; the fixture also expires automatically. Live tests are independent of the normal unit tests and require an explicit script invocation.

Diagnostic-only lifecycle, without the native plugin helper:

```powershell
& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\tests\fixture\Test-FixtureCycle.ps1
```

This diagnostic checks standard-control messages and `PrintWindow` against the owned target. It does **not** establish WGC or plugin integration. Readiness and worker timeouts are each 15 seconds, normal stop waits up to five seconds, and the fixture expires after 60 seconds.

Actual native helper acceptance, including occluded WGC capture, standard Edit/Button actions, coordinate-selected button action, stale identities, and minimized capture rejection:

```powershell
& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\tests\fixture\Test-NativeBackend.ps1 -HelperPath 'D:\Tools\BackgroundComputerUse\native\BackgroundComputerUse.Native.exe'
```

Each helper process is limited to ten seconds, the fixture expires after 120 seconds, and normal stop waits up to five seconds. WGC requests have a four-second internal frame timeout. The test asks the fixture to minimize itself and verifies rejection without restoration. It records the helper's SHA-256 but does not configure the production plugin.

Actual Python core and MCP stdio acceptance:

```powershell
py -3 .\tests\fixture\test_core_mcp_e2e.py --helper 'D:\Tools\BackgroundComputerUse\native\BackgroundComputerUse.Native.exe' --sha256 '5ce953bb393d7a0ac0513cf2abb0186d5331ff358d349e6c35a2ef8e027318a8'
```

Supply the reviewed hash of a different build through `--sha256`; the test rejects a changed helper. `--helper` overrides the D: default. The plugin root defaults to the nearest ancestor containing `server.py` and `bcu/`; `--plugin-root <absolute-path>` overrides it. Receipts default to the external data directory described below; `--run-root <absolute-path>` overrides it. The Python launcher waits at most 20 seconds for each PowerShell lifecycle script, native calls are limited to 12 seconds, MCP responses to 15 seconds, and the owned fixture expires after 120 seconds. The test proves fresh control readback, consumed-observation rejection, physical image mapping and PNG hash, seven MCP tools, and a valid MCP image content block. Foreground handoff is prepared as metadata with `input_executed: false`; this test sends no foreground input.

To keep a fixture briefly for another explicitly authorized test, run this in **Windows PowerShell 5.1** and always keep the `finally` cleanup:

```powershell
$fixture = Join-Path (Get-Location) 'tests\fixture'
$launch = & "$fixture\Start-Fixture.ps1" -LifetimeSeconds 180 | ConvertFrom-Json
try {
    Get-Content -Raw "$($launch.runDirectory)\ready.json"
    # Run explicitly chosen fixture-only checks here.
} finally {
    & "$fixture\Stop-Fixture.ps1" -RunDirectory $launch.runDirectory
}
```

Standalone lifetime accepts 15–900 seconds. A readiness timeout writes a stop request. Stop never kills another app or moves/closes any user-owned window. `Stop-Fixture.ps1` is idempotent once `stopped.json` exists.

Each run creates `<run-root>/<instance-id>/`. The default run root is `$env:BCU_DATA_DIR\self-tests` when `BCU_DATA_DIR` is set, otherwise `~/.codex/plugin-data/background-computer-use/self-tests`. No generated receipts, captures, configuration, or last-run marker are written into the plugin cache by default. Pass `-RunRoot <absolute-path>` to any PowerShell start/lifecycle/native-test script, or `--run-root <absolute-path>` to the Python test, to choose another location.

`ready.json` provides exact HWNDs, PID, process start time, control IDs, painted colors, live text/counter readback, and stop/expiry paths. `targetIdentity` and `coverIdentity` contain `{hwnd, pid, start_time}` with HWND and UTC ticks stored as precision-preserving decimal strings. `coordinateSpace` is `physical-screen-pixels`; only the fixture/harness's own thread DPI context is changed. Window/process ownership and exact creation time are checked before fixture actions.

Foreground HWND, physical `GetCursorPos`, and `GetLastInputInfo` are independently sampled. External activity or state changes make a preservation observation inconclusive, without attributing them to code. Sampling cannot exclude changes between samples. Generic third-party UIA providers or applications may change focus themselves, and ordinary mouse messages may be ignored; a successful API return is not counted as confirmed app behavior. See [recorded verification](TEST_RESULTS.md) for the completed acceptance scope.

Generated runs, image captures, logs, caches, and helper binaries are excluded from the reusable fixture source package. The harnesses intentionally contain no `unittest.TestCase`, and this directory is not a Python package for recursive unittest discovery.
