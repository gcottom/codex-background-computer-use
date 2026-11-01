# Recorded acceptance — 2026-10-30

Actual Python core and MCP stdio acceptance passed with native helper SHA-256 `5CE953BB393D7A0AC0513CF2ABB0186D5331FF358D349E6C35A2EF8E027318A8`. Fresh Edit readback matched the requested text, the button counter reached 2, reused observations were rejected, seven MCP tools were listed, and MCP returned a PNG matching its structured capture hash and physical geometry. Both foreground handoff preparations returned `input_executed: false`.

An independent sampler recorded 2,201 samples across the complete 12.72-second lifecycle with unchanged foreground HWND, physical pointer, and last-input tick. Target capture contained its known painted colors and zero cover background pixels. The final image was visually inspected. The fixture stopped normally.

Direct native acceptance separately passed standard Edit/Button actions under full occlusion, WGC target/cover pixel separation, stale PID/creation-time/runtime-ID rejection, and minimized capture rejection without restore or output. Testing exposed the standard Edit UIA provider's activation side effect and led to the identity-checked standard-control message routes. Generic provider behavior remains application-dependent.

The direct native test was repeated from this packaged fixture directory against the final `5CE953...318A8` helper build. All ten steps passed; control readback reached counter 2, foreground/pointer/input state stayed unchanged, and normal cleanup completed. Parsing, Python help output, and automatic plugin-root resolution were also checked after packaging.

Full receipts and captures are retained outside this source directory. The plugin's validation record identifies those artifacts; generated runs and machine-specific artifact links are intentionally absent from this package. The commands in [README.md](README.md) create fresh receipts when explicitly run.
