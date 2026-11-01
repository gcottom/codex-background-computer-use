# Third-party components

The Python orchestration uses the standard library. The native helper uses Microsoft WinApp UIAutomation 0.7.1, Windows UI Automation, WGC, .NET and Windows Desktop libraries. Versions, package content hashes, SDK provenance and license links are documented in [native/THIRD_PARTY.md](native/THIRD_PARTY.md) and [native/packages.lock.json](native/packages.lock.json).

The published self-contained helper is installed separately under `D:\Tools\BackgroundComputerUse`, with its private SDK, dependency cache and build receipts. It is not downloaded when a tool is called. Configuration records its executable hash; changing the build requires configuring the reviewed executable again.

The high-level WinApp screenshot command is intentionally unused because its recovery can restore or foreground windows. This helper uses the public WGC frame-grabber API directly, with no foreground capture fallback.
