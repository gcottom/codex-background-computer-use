# Foreground mouse handoff

Background operations are the default. A separate physical mouse route is available through the installed Computer Use plugin when the user chooses it for the task. `prepare_foreground_mouse` validates the native target and returns identifying metadata; it sends no input.

Read the installed Computer Use skill and its guidance, API and confirmations documents. Initialize its supported `@oai/sky` package through `node_repl`, enumerate with `sky.list_windows()` or `sky.list_apps()`, and select one actual returned window matching the intended application/title. If matching is ambiguous, inspect the candidates before acting. Rehydrate using `sky.get_window({id,app})`, then capture `sky.get_window_state({window,include_screenshot:true,include_text:true})`.

Inspect that state before sending one mouse action through `sky.click`, `sky.drag`, `sky.scroll` or other documented APIs. Refresh immediately afterward. Sky input methods activate the target and can move the user's physical pointer. Do not use them as an automatic background fallback.

Sky window IDs are opaque. This plugin's HWND and opaque window_id are not Sky IDs. Sky screenshot geometry is documented in logical pixels; this plugin's verified capture mapping uses physical screen pixels. Do not copy coordinates between these systems or derive a conversion from a guessed scale. Use each runtime's own fresh screenshot/element references.

Read-only Sky state capture can be useful as a separately identified observation route without calling `activate_window`. Its full minimized/activation behavior is not established by this plugin's strict WGC test, so do not silently substitute it for the strict background capture guarantee.
