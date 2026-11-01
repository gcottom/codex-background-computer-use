# Release validation — 0.1.9

## Summary

- **39 Python/core/server tests passed.** Coverage includes UIA/message action validation, observation lifetime and replay protection, foreground restoration, input-activity handling, pointer movement, changed foreground windows, MCP schemas, and the delayed handback path.
- **Native helper 1.7.0 built** with locked NuGet dependencies using .NET SDK 10.0.403. SHA-256 for the release-checkout build: `7C30EF6D42D95D11584835B38C150995BA0B985BD19F5EBA20717E463E956A2C`. Assembly version metadata matches helper version 1.7.0 and does not include a checkout-specific Git revision.
- **Live VirtualBox Manager test passed.** Selecting the Home view caused the already-open Manager to activate and switch desktops. The first native call restored the saved desktop in 61 ms; an immediate foreground-only retry before fresh screenshot capture verified the exact saved ChatGPT HWND in 58 ms. The final focus summary confirmed both desktop restoration and foreground verification.

## Keyboard activity and focus safeguards

The earlier behavior stopped a requested handback whenever LastInputInfo changed. During one Home-view test, the user was typing while VirtualBox had taken focus; this guard left the Manager foreground. Version 0.1.9 allows an explicitly requested handback to continue through keyboard activity only while the cursor is stationary and the exact action-activated HWND remains foreground. It reports `user_input_since_observation` so callers can see that input occurred.

The typing-tolerance rule is covered by regression tests, including a changed-foreground rejection case. The final live handback run itself had no concurrent keyboard input. Pointer movement or a different foreground HWND still stops a handback. The separate minimize fallback remains opt-in and was not exercised live in this release.

## How to reproduce

Run the Python/core/server suite from `plugins/background-computer-use`:

```powershell
py -3 -m unittest discover -s tests -v
```

Build the native helper using the pinned .NET SDK and locked package graph:

```powershell
Set-Location .\plugins\background-computer-use
.\native\Build.ps1
```

The marketplace contains source and does not bundle or automatically install the native executable. Configure a locally built helper with `configure_backend` before running capture or control acceptance tests. Prior owned-window fixture receipts and release-specific iteration notes are retained in [VALIDATION_HISTORY.md](VALIDATION_HISTORY.md).

## Supported scope

WGC capture requires Windows 10 build 19041 or later. Virtual-desktop restoration uses internal, version-sensitive Shell interfaces and requires a supported Windows 11 22H2+ build. Apps can ignore background messages, activate themselves, or require raw/foreground input. Minimized, protected, secure-desktop, and non-rendering targets may be unavailable. A delivered window message is not proof of application success; inspect the returned state or image.
