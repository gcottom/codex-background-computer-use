# Capture, control and evidence limits

Strict capture calls Microsoft WinApp UIAutomation's low-level `IWindowCapture.StartFrameGrabber` using Windows Graphics Capture. It never calls high-level `ScreenshotAsync` or `CaptureWindowPixels`: those routes can restore or foreground a target during recovery. An unavailable WGC frame returns an explicit error; capture does not change window visibility, focus or position.

Covered and minimized are different states. A covered window can continue rendering into a capture surface. A minimized, suspended, protected, non-rendering, exclusive-mode or inaccessible window may produce no usable frame. A screenshot shows pixels; it does not establish responsiveness or feature success. No display adapter, power, lid, VM or security settings are changed to obtain a frame.

Background semantic actions use exact control identities and supported patterns. Standard native edit/button controls can use scoped text/button messages to avoid accessibility providers that focus the app. Providers and application event handlers may nevertheless activate windows, open dialogs or change their own state. Desktop samples report observed changes; simultaneous user activity can change them too. No global focus lock or automatic focus restoration is used.

Window-message mouse actions are cooperative input, not DirectInput/raw-input emulation. Apps can ignore them, especially games, custom canvases and GPU interfaces. Message delivery and intended behavior remain separate. Physical mouse input is available through the explicit foreground handoff.

UIA, WGC and window messages respect process/session/desktop access. The plugin does not elevate, attach input queues, bypass UIPI, automate credentials or inject code into another process. Password values are omitted and password controls cannot be acted on. A locked or secure desktop is a blocker.

The plugin binds HWND, PID and process start identity; checks geometry and control identity again; limits image size and tree traversal; serializes its own per-window mutations; and consumes action observations before dispatch. Separate tools or the user can still change an app concurrently, so verify the fresh result after each action.

Primary references: [Microsoft UI Automation](https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/ui-automation), [WGC frame pools](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool), and [pinned WinApp source](https://github.com/microsoft/WinAppCli/tree/e3cd0b94f3e7b776ac51ba0564f4908094e979b5).
