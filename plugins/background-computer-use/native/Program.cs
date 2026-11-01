using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace BackgroundComputerUse.Native;

internal sealed class ApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal readonly record struct Rect(int x, int y, int width, int height);
internal readonly record struct Bounds(double x, double y, double width, double height);
internal readonly record struct Cursor(int x, int y);
internal sealed record DesktopSnapshot(string foreground_hwnd, Cursor cursor, uint? last_input_tick, int session_id,
    string process_desktop, string input_desktop, bool interactive);
internal sealed record ForegroundWindow(string hwnd, int pid, string start_time, string title, string class_name,
    int session_id, string? virtual_desktop_id, bool? on_current_virtual_desktop);
internal readonly record struct WindowSurface(nint Hwnd, int Pid, string StartTime, string Title, string ClassName,
    Rect WindowRect, Rect ClientRect, string Route);

internal static class Program
{
    private const int MaxPixels = 20_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };
    private static readonly HashSet<string> ActionNames = ["invoke", "set_value", "toggle", "select", "expand", "collapse", "scroll"];

    [STAThread]
    public static int Main()
    {
        try
        {
            var input = Console.In.ReadToEnd();
            if (input.Length > 1_000_000) throw new ApiException("invalid_arguments", "Request exceeds 1 MB.");
            using var document = JsonDocument.Parse(input);
            var request = document.RootElement;
            var verb = String(request, "verb");
            var result = verb switch
            {
                "status" => Status(),
                "list" => List(),
                "inspect" => Inspect(request),
                "capture" => Capture(request).GetAwaiter().GetResult(),
                "act" => Act(request),
                "click" => Click(request),
                "restore_foreground" => RestoreForeground(request),
                _ => throw new ApiException("invalid_arguments", "Unknown verb.")
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            var error = exception switch
            {
                ApiException api => new { code = api.Code, message = api.Message },
                ElementNotAvailableException => new { code = "stale_element", message = "The UIA element is no longer available. Inspect again." },
                UnauthorizedAccessException => new { code = "access_denied", message = "The target or output cannot be accessed at this integrity level." },
                JsonException => new { code = "invalid_arguments", message = "The request is not valid JSON." },
                OperationCanceledException => new { code = "timeout", message = "The operation timed out." },
                _ => new { code = "native_error", message = exception.Message }
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(new { error }, JsonOptions));
            return 1;
        }
    }

    private static string String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ApiException("invalid_arguments", $"'{name}' must be a string.");

    private static int Integer(JsonElement root, string name, int fallback, int minimum, int maximum)
    {
        var value = root.TryGetProperty(name, out var item) ? item.GetInt32() : fallback;
        if (value < minimum || value > maximum) throw new ApiException("invalid_arguments", $"'{name}' is outside the supported range.");
        return value;
    }

    private static nint Handle(string value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? (nint)number : throw new ApiException("invalid_arguments", "HWND must be a positive decimal string.");

    private static string HandleText(nint value) => ((long)value).ToString(CultureInfo.InvariantCulture);

    private static object Status()
    {
        using var services = Services();
        return new { name = "background-computer-use-native", version = "1.7.0", platform = "windows", architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            wgc_supported = services.GetRequiredService<IWindowCapture>().IsFrameCaptureSupported,
            uia_backend = "Windows UIAutomationClient", capture_backend = "Microsoft WinApp UIAutomation 0.7.1 WGC-only",
            message_click_supported = true, guarded_foreground_restore_supported = true, desktop = Desktop(false) };
    }

    private static ServiceProvider Services()
        => new ServiceCollection().AddLogging().AddWinAppUiAutomation().BuildServiceProvider();

    private static DesktopSnapshot Desktop(bool requireInteractive = true)
    {
        var processDesktop = Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
        var inputHandle = Native.OpenInputDesktop(0, false, 0x0001);
        string inputDesktop;
        try { inputDesktop = inputHandle == 0 ? "unavailable" : Native.DesktopName(inputHandle); }
        finally { if (inputHandle != 0) Native.CloseDesktop(inputHandle); }
        var interactive = processDesktop == "Default" && inputDesktop == processDesktop && Environment.UserInteractive;
        if (requireInteractive && !interactive) throw new ApiException("no_interactive_desktop", "The process must share the unlocked Default input desktop.");
        if (!Native.GetCursorPos(out var position)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var input = new Native.LastInput { Size = (uint)Marshal.SizeOf<Native.LastInput>() };
        uint? lastInput = Native.GetLastInputInfo(ref input) ? input.Tick : null;
        return new DesktopSnapshot(HandleText(Native.GetForegroundWindow()), new Cursor(position.X, position.Y), lastInput,
            Process.GetCurrentProcess().SessionId, processDesktop, inputDesktop, interactive);
    }

    private static object List()
    {
        var desktop = Desktop();
        var windows = new List<object>();
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0) return true;
                using var process = Process.GetProcessById((int)pid);
                if (process.SessionId != Process.GetCurrentProcess().SessionId) return true;
                if (!Native.IsWindowVisible(hwnd)) return true;
                windows.Add(Window(hwnd, process));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { }
            return true;
        }, 0);
        return new { windows, desktop };
    }

    private static object Window(nint hwnd, Process process)
        => new { hwnd = HandleText(hwnd), pid = process.Id, start_time = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            title = Native.WindowTitle(hwnd), class_name = Native.ClassName(hwnd), minimized = Native.IsIconic(hwnd), visible = Native.IsWindowVisible(hwnd),
            client_rect = Native.ClientRect(hwnd), window_rect = Native.WindowRect(hwnd) };

    private static (nint Hwnd, Process Process) Bind(JsonElement request, bool rejectMinimized = false)
    {
        _ = Desktop();
        var hwnd = Handle(String(request, "hwnd"));
        var expectedPid = Integer(request, "pid", 0, 1, int.MaxValue);
        var expectedStart = String(request, "start_time");
        if (!Native.IsWindow(hwnd)) throw new ApiException("window_closed", "The bound HWND no longer exists.");
        Native.GetWindowThreadProcessId(hwnd, out var currentPid);
        if (currentPid != expectedPid) throw new ApiException("window_identity_mismatch", "The HWND belongs to a different process.");
        var process = Process.GetProcessById(expectedPid);
        try
        {
            if (process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) != expectedStart)
                throw new ApiException("window_identity_mismatch", "The process creation time changed.");
            if (process.SessionId != Process.GetCurrentProcess().SessionId)
                throw new ApiException("wrong_session", "The target belongs to another Windows session.");
            if (rejectMinimized && Native.IsIconic(hwnd)) throw new ApiException("window_minimized", "Minimized windows are never restored by background operations.");
            return (hwnd, process);
        }
        catch { process.Dispose(); throw; }
    }

    private static AutomationElement Root(nint hwnd) => AutomationElement.FromHandle(hwnd)
        ?? throw new ApiException("uia_unavailable", "No UI Automation root is available for this HWND.");

    // TreeWalker stays inside the explicitly bound root. Both depth and node count are bounded.
    private static IEnumerable<(AutomationElement Element, int Depth)> Walk(AutomationElement root, int depth, int maxNodes)
    {
        var pending = new Stack<(AutomationElement, int)>();
        pending.Push((root, 0));
        var count = 0;
        var discovered = 1;
        while (pending.Count > 0 && count++ < maxNodes)
        {
            var current = pending.Pop();
            yield return current;
            if (current.Item2 >= depth) continue;
            var children = new List<AutomationElement>();
            var child = TreeWalker.ControlViewWalker.GetFirstChild(current.Item1);
            while (child is not null && discovered < maxNodes)
            {
                children.Add(child);
                discovered++;
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            for (var index = children.Count - 1; index >= 0; --index) pending.Push((children[index], current.Item2 + 1));
        }
    }

    private static Bounds ElementBounds(System.Windows.Rect bounds)
        => bounds.IsEmpty ? new(0, 0, 0, 0) : new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private static object Element(AutomationElement element, int depth)
    {
        var current = element.Current;
        var runtimeId = element.GetRuntimeId();
        string? value = null;
        if (!current.IsPassword && element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            value = ((ValuePattern)valuePattern).Current.Value;
        var valueTruncated = value is not null && value.Length > 4096;
        if (value is not null) value = Limited(value, 4096);
        return new { element_id = string.Join(".", runtimeId), runtime_id = runtimeId, depth,
            name = Limited(current.Name, 4096), automation_id = Limited(current.AutomationId, 512), control_type = current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
            enabled = current.IsEnabled, offscreen = current.IsOffscreen, is_password = current.IsPassword,
            value, value_truncated = valueTruncated,
            bounds = ElementBounds(current.BoundingRectangle),
            patterns = element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName.Replace("Identifiers.Pattern", "")).ToArray() };
    }

    private static string Limited(string value, int maximum) => value.Length > maximum ? value[..maximum] : value;

    private static object Inspect(JsonElement request)
    {
        var before = Desktop();
        var bound = Bind(request);
        using var process = bound.Process;
        var depth = Integer(request, "depth", 6, 0, 32);
        var maximum = Integer(request, "max_nodes", 300, 1, 5000);
        var elements = new List<object>();
        var truncated = false;
        foreach (var node in Walk(Root(bound.Hwnd), depth, maximum + 1))
        {
            if (elements.Count == maximum) { truncated = true; break; }
            elements.Add(Element(node.Element, node.Depth));
            if (node.Depth == depth && TreeWalker.ControlViewWalker.GetFirstChild(node.Element) is not null) truncated = true;
        }
        var surfaces = SurfaceCandidates(bound.Hwnd, process)
            .Select(surface => new { hwnd = HandleText(surface.Hwnd), pid = surface.Pid, start_time = surface.StartTime,
                title = surface.Title, class_name = surface.ClassName, window_rect = surface.WindowRect,
                client_rect = surface.ClientRect, route = surface.Route }).ToArray();
        var after = Desktop();
        return new { window = Window(bound.Hwnd, process), elements, surfaces, truncated, desktop = after, before, after };
    }

    private static async Task<object> Capture(JsonElement request)
    {
        var hasSurface = request.TryGetProperty("surface_hwnd", out var surfaceHwndJson);
        var bound = Bind(request, rejectMinimized: !hasSurface);
        using var process = bound.Process;
        var path = Path.GetFullPath(String(request, "path"));
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ApiException("invalid_arguments", "Capture output must be a PNG path.");
        if (File.Exists(path)) throw new ApiException("output_exists", "Capture never overwrites an existing file.");
        var timeout = Integer(request, "timeout_ms", 3000, 100, 5000);
        var before = Desktop();
        var rootWindowRect = Native.WindowRect(bound.Hwnd);
        var rootClientRect = Native.ClientRect(bound.Hwnd);
        WindowSurface? surface = null;
        var captureHwnd = bound.Hwnd;
        if (hasSurface)
        {
            var expectedHwnd = Handle(surfaceHwndJson.GetString() ?? "");
            var expectedPid = Integer(request, "surface_pid", 0, 1, int.MaxValue);
            var expectedStart = String(request, "surface_start_time");
            var matches = SurfaceCandidates(bound.Hwnd, process)
                .Where(item => item.Hwnd == expectedHwnd && item.Pid == expectedPid && item.StartTime == expectedStart).Take(2).ToArray();
            if (matches.Length != 1) throw new ApiException("stale_surface", "The child surface identity changed or is no longer visible; inspect again.");
            surface = matches[0];
            captureHwnd = surface.Value.Hwnd;
        }
        Native.GetWindowThreadProcessId(captureHwnd, out var actualCapturePid);
        if (actualCapturePid == 0 || (surface is not null && actualCapturePid != surface.Value.Pid))
            throw new ApiException("window_identity_mismatch", "The capture surface owner process changed.");
        var captureWindowRect = Native.WindowRect(captureHwnd);
        var captureClientRect = Native.ClientRect(captureHwnd);
        var captureExtendedRect = Native.ExtendedRect(captureHwnd);
        if ((long)captureWindowRect.width * captureWindowRect.height > MaxPixels) throw new ApiException("capture_too_large", "Capture exceeds 20 megapixels.");
        using var services = Services();
        var capture = services.GetRequiredService<IWindowCapture>();
        if (!capture.IsFrameCaptureSupported) throw new ApiException("wgc_unavailable", "WGC is unavailable. Background capture has no fallback.");
        using var cancellation = new CancellationTokenSource(timeout);
        using var grabber = capture.StartFrameGrabber(captureHwnd, fps: 10);
        if (!await grabber.WaitForFirstFrameAsync(TimeSpan.FromMilliseconds(timeout), cancellation.Token))
            throw new ApiException("capture_timeout", "No WGC frame arrived. No restore or fallback was attempted.");
        var frame = grabber.TryGetLatest();
        // A first WGC frame can be a transient all-zero surface. Wait briefly for useful pixels.
        while (frame is not null && Blank(frame.Value.Pixels) && !grabber.IsClosed)
        {
            await Task.Delay(50, cancellation.Token);
            frame = grabber.TryGetLatest();
        }
        if (grabber.IsClosed || frame is null) throw new ApiException("capture_unavailable", "The WGC session closed without a frame.");
        var pixels = frame.Value;
        if (pixels.Width <= 0 || pixels.Height <= 0 || (long)pixels.Width * pixels.Height > MaxPixels)
            throw new ApiException("capture_too_large", "WGC frame is empty or exceeds 20 megapixels.");
        using (var confirmation = Bind(request, rejectMinimized: !hasSurface).Process) { }
        if (Native.WindowRect(bound.Hwnd) != rootWindowRect || Native.ClientRect(bound.Hwnd) != rootClientRect)
            throw new ApiException("target_moved", "Window geometry changed during capture. Capture again.");
        var surfaceStillValid = surface is null || SurfaceCandidates(bound.Hwnd, process)
            .Any(item => item.Hwnd == surface.Value.Hwnd && item.Pid == surface.Value.Pid && item.StartTime == surface.Value.StartTime
                && item.WindowRect == surface.Value.WindowRect && item.ClientRect == surface.Value.ClientRect);
        if (!Native.IsWindow(captureHwnd) || !surfaceStillValid
            || Native.WindowRect(captureHwnd) != captureWindowRect || Native.ClientRect(captureHwnd) != captureClientRect)
            throw new ApiException("target_moved", "The capture surface moved or changed relationship during capture.");
        Native.GetWindowThreadProcessId(captureHwnd, out var confirmedCapturePid);
        if (confirmedCapturePid != actualCapturePid) throw new ApiException("window_identity_mismatch", "The capture surface owner process changed during capture.");
        using (var captureOwner = Process.GetProcessById((int)confirmedCapturePid))
            if (surface is not null && captureOwner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) != surface.Value.StartTime)
                throw new ApiException("window_identity_mismatch", "The child surface process changed during capture.");
        Rect? screenRect = captureExtendedRect is Rect extended && extended.width == pixels.Width && extended.height == pixels.Height ? extended
            : captureWindowRect.width == pixels.Width && captureWindowRect.height == pixels.Height ? captureWindowRect : null;
        var image = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Pixels, pixels.Width * 4);
        image.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
        var after = Desktop();
        var capturedSurface = surface is null ? null : new { hwnd = HandleText(surface.Value.Hwnd), pid = surface.Value.Pid,
            start_time = surface.Value.StartTime, title = surface.Value.Title, class_name = surface.Value.ClassName, route = surface.Value.Route };
        return new { path, width = pixels.Width, height = pixels.Height, method = "wgc-only", before, after, surface = capturedSurface,
            geometry = new { coordinate_space = "physical-screen-pixels", window_rect = captureWindowRect, client_rect = captureClientRect,
                extended_frame_rect = captureExtendedRect, frame_rect = new Rect(0, 0, pixels.Width, pixels.Height),
                screen_rect = screenRect, geometry_verified = screenRect is not null } };
    }

    private static bool Blank(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
            if (pixels[index] > 2 || pixels[index + 1] > 2 || pixels[index + 2] > 2) return false;
        return true;
    }

    private static object Act(JsonElement request)
    {
        var action = String(request, "action");
        if (!ActionNames.Contains(action)) throw new ApiException("invalid_arguments", "Unsupported semantic action.");
        var bound = Bind(request);
        using var process = bound.Process;
        ConfirmExpectedWindow(request, bound.Hwnd);
        var runtimeId = request.GetProperty("runtime_id").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        if (runtimeId.Length is < 1 or > 64) throw new ApiException("invalid_arguments", "Invalid UIA runtime identity.");
        var expected = request.GetProperty("expected");
        var element = Walk(Root(bound.Hwnd), 64, 10_000).Select(item => item.Element)
            .FirstOrDefault(item => item.GetRuntimeId().SequenceEqual(runtimeId))
            ?? throw new ApiException("stale_element", "The runtime identity no longer exists inside the bound window.");
        var current = element.Current;
        if (current.ProcessId != process.Id) throw new ApiException("element_process_mismatch", "The UIA element belongs to a different process.");
        if (current.IsPassword) throw new ApiException("password_control", "Password controls cannot be read or changed through this plugin.");
        if (current.Name != String(expected, "name") || current.AutomationId != String(expected, "automation_id") ||
            current.ControlType.ProgrammaticName.Replace("ControlType.", "") != String(expected, "control_type"))
            throw new ApiException("stale_element", "The element metadata changed. Inspect again.");
        if (!current.IsEnabled) throw new ApiException("element_disabled", "The UIA element is disabled.");
        if (request.TryGetProperty("expected_bounds", out var expectedBounds) && expectedBounds.Deserialize<Bounds>() != ElementBounds(current.BoundingRectangle))
            throw new ApiException("target_moved", "The UIA element bounds changed. Inspect again.");
        var foregroundBefore = Native.ForegroundWindowIdentity();
        var before = Desktop();
        var actionProcessPath = Native.ProcessImagePath(process);
        using (var confirmation = Bind(request).Process) { }
        ConfirmExpectedWindow(request, bound.Hwnd);
        string pattern;
        var deliveryRoute = "uia-pattern";
        switch (action)
        {
            case "invoke":
                if (NativeControl(element, bound.Hwnd, process.Id, out var buttonHwnd, out var buttonClass) && Native.IsButton(buttonClass))
                {
                    Send(buttonHwnd, 0x00F5, 0, 0); deliveryRoute = "bm_click";
                }
                else Pattern<InvokePattern>(element, InvokePattern.Pattern).Invoke();
                pattern = "InvokePattern"; break;
            case "set_value":
                var value = String(request, "value");
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                {
                    var editable = (ValuePattern)valuePattern;
                    if (editable.Current.IsReadOnly) throw new ApiException("read_only", "The value is read-only.");
                    if (NativeControl(element, bound.Hwnd, process.Id, out var editHwnd, out var editClass) && Native.IsEdit(editClass))
                    {
                        var style = Native.GetWindowLong(editHwnd, -16);
                        if ((style & 0x0020) != 0) throw new ApiException("password_control", "The native edit control is a password field.");
                        if ((style & 0x0800) != 0) throw new ApiException("read_only", "The native edit control is read-only.");
                        if (Native.SendMessageTimeoutText(editHwnd, 0x000C, 0, value, 3, 1000, out var accepted) == 0 || accepted == 0)
                            throw new ApiException("message_delivery_failed", "WM_SETTEXT was rejected or timed out.");
                        deliveryRoute = "wm_settext";
                    }
                    else editable.SetValue(value);
                    pattern = "ValuePattern";
                }
                else
                {
                    var numeric = Pattern<RangeValuePattern>(element, RangeValuePattern.Pattern);
                    if (numeric.Current.IsReadOnly || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                        throw new ApiException("invalid_value", "The numeric value is invalid or read-only.");
                    numeric.SetValue(number); pattern = "RangeValuePattern";
                }
                break;
            case "toggle": Pattern<TogglePattern>(element, TogglePattern.Pattern).Toggle(); pattern = "TogglePattern"; break;
            case "select": Pattern<SelectionItemPattern>(element, SelectionItemPattern.Pattern).Select(); pattern = "SelectionItemPattern"; break;
            case "expand": Pattern<ExpandCollapsePattern>(element, ExpandCollapsePattern.Pattern).Expand(); pattern = "ExpandCollapsePattern"; break;
            case "collapse": Pattern<ExpandCollapsePattern>(element, ExpandCollapsePattern.Pattern).Collapse(); pattern = "ExpandCollapsePattern"; break;
            case "scroll":
                var direction = String(request, "direction");
                var scroll = Pattern<ScrollPattern>(element, ScrollPattern.Pattern);
                var horizontal = direction == "left" ? ScrollAmount.SmallDecrement : direction == "right" ? ScrollAmount.SmallIncrement : ScrollAmount.NoAmount;
                var vertical = direction == "up" ? ScrollAmount.SmallDecrement : direction == "down" ? ScrollAmount.SmallIncrement : ScrollAmount.NoAmount;
                if (horizontal == ScrollAmount.NoAmount && vertical == ScrollAmount.NoAmount) throw new ApiException("invalid_arguments", "Scroll direction must be up, down, left or right.");
                scroll.Scroll(horizontal, vertical); pattern = "ScrollPattern"; break;
            default: throw new ApiException("invalid_arguments", "Unknown action.");
        }
        var after = Desktop();
        var minimizeActivatingWindow = request.TryGetProperty("minimize_activating_window", out var minimizeAction)
            && minimizeAction.ValueKind == JsonValueKind.True;
        var focusRestoration = RestoreAfterAction(request, foregroundBefore, before, after, actionProcessPath);
        return new { performed = true, action, pattern, delivery_route = deliveryRoute, before,
            after = Desktop(), action_after = after, focus_restoration = focusRestoration,
            foreground_before = foregroundBefore, action_process_path = actionProcessPath,
            minimize_activating_window = minimizeActivatingWindow, hwnd = HandleText(bound.Hwnd), runtime_id = runtimeId };
    }

    private static object RestoreForeground(JsonElement request)
    {
        var previous = request.GetProperty("previous_foreground").Deserialize<ForegroundWindow>(JsonOptions);
        var expectedCurrent = String(request, "expected_foreground_hwnd");
        var expectedLastInput = request.TryGetProperty("expected_last_input_tick", out var tickJson) && tickJson.ValueKind == JsonValueKind.Number
            ? tickJson.GetUInt32() : (uint?)null;
        var expectedCursor = request.TryGetProperty("expected_cursor", out var cursorJson) && cursorJson.ValueKind == JsonValueKind.Object
            ? cursorJson.Deserialize<Cursor>(JsonOptions) : (Cursor?)null;
        var expectedSession = Integer(request, "expected_session_id", 0, 0, int.MaxValue);
        var expectedProcessDesktop = String(request, "expected_process_desktop");
        var expectedInputDesktop = String(request, "expected_input_desktop");
        var actionProcessPath = request.TryGetProperty("action_process_path", out var pathJson) && pathJson.ValueKind == JsonValueKind.String
            ? pathJson.GetString() : null;
        var minimizeActivatingWindow = request.TryGetProperty("minimize_activating_window", out var minimizeJson)
            && minimizeJson.ValueKind == JsonValueKind.True;
        var before = Desktop();
        return RestoreForeground(previous, expectedCurrent, expectedLastInput, expectedCursor, expectedSession,
            expectedProcessDesktop, expectedInputDesktop, actionProcessPath, minimizeActivatingWindow, before);
    }

    private static object RestoreForeground(ForegroundWindow? previous, string expectedCurrent, uint? expectedLastInput,
        Cursor? expectedCursor, int expectedSession, string expectedProcessDesktop, string expectedInputDesktop,
        string? actionProcessPath, bool minimizeActivatingWindow, DesktopSnapshot before)
    {
        var restoreTimer = Stopwatch.StartNew();
        object Skipped(string reason) => new { attempted = false, restored = false, reason,
            user_input_since_observation = before.last_input_tick != expectedLastInput,
            restore_elapsed_ms = restoreTimer.Elapsed.TotalMilliseconds, previous_foreground = previous, before, after = before };
        if (previous is null) return Skipped("previous_foreground_unavailable");
        if (expectedLastInput is null || expectedCursor is null) return Skipped("user_activity_baseline_unavailable");
        if (before.foreground_hwnd == previous.hwnd)
            return new { attempted = false, restored = true, reason = "foreground_unchanged", restore_elapsed_ms = restoreTimer.Elapsed.TotalMilliseconds, previous_foreground = previous, before, after = before };
        if (before.foreground_hwnd != expectedCurrent) return Skipped("foreground_changed_again");
        if (before.cursor != expectedCursor) return Skipped("cursor_moved_since_observation");
        if (before.session_id != expectedSession || before.process_desktop != expectedProcessDesktop || before.input_desktop != expectedInputDesktop)
            return Skipped("desktop_or_session_changed");
        if (!TryValidateForegroundWindow(previous, out var previousHwnd, out var validationReason)) return Skipped(validationReason);
        if (previous.on_current_virtual_desktop != true) return Skipped("previous_window_desktop_unavailable");
        var desktopSwitched = false;
        var onCurrentDesktop = Native.IsWindowOnCurrentVirtualDesktop(previousHwnd);
        if (onCurrentDesktop != true)
        {
            if (!Guid.TryParse(previous.virtual_desktop_id, out var previousDesktopId)) return Skipped("previous_window_desktop_unavailable");
            if (!long.TryParse(expectedCurrent, NumberStyles.None, CultureInfo.InvariantCulture, out var rawCurrent)
                || Native.IsWindowOnCurrentVirtualDesktop((nint)rawCurrent) != true)
                return Skipped("current_action_desktop_unavailable");
            var actionDesktopId = Native.GetWindowDesktopGuid((nint)rawCurrent);
            if (actionDesktopId is null || actionDesktopId == previousDesktopId) return Skipped("desktop_identity_unavailable");
            if (!Native.TrySwitchVirtualDesktop(previousDesktopId, out var switchReason))
                return Skipped(switchReason);
            desktopSwitched = true;
            before = Desktop();
            onCurrentDesktop = Native.IsWindowOnCurrentVirtualDesktop(previousHwnd);
            if (onCurrentDesktop != true) return new { attempted = false, restored = false, reason = "previous_desktop_switch_unverified",
                desktop_switched = true, previous_foreground = previous, before, after = before, previous_window_on_current_virtual_desktop = onCurrentDesktop };
            if (before.cursor != expectedCursor) return new { attempted = false, restored = false, reason = "cursor_moved_during_desktop_switch",
                desktop_switched = true, previous_foreground = previous, before, after = before, previous_window_on_current_virtual_desktop = onCurrentDesktop };
        }

        var after = Desktop();
        var switchToThisWindowCalled = false;
        var actionWindowStillForeground = after.foreground_hwnd == expectedCurrent;
        var shellActivated = false;
        var shellActivationReason = "activation_guard_changed";
        if (actionWindowStillForeground && after.cursor == expectedCursor)
            shellActivated = Native.TrySwitchToApplicationView(previousHwnd, out shellActivationReason);
        after = Desktop();
        if (after.foreground_hwnd != previous.hwnd && after.foreground_hwnd == expectedCurrent && after.cursor == expectedCursor)
        {
            Native.SwitchToThisWindow(previousHwnd, true);
            switchToThisWindowCalled = true;
            after = Desktop();
        }
        var canRequestForeground = after.foreground_hwnd == expectedCurrent && after.cursor == expectedCursor;
        var accepted = after.foreground_hwnd == previous.hwnd || (canRequestForeground && Native.SetForegroundWindow(previousHwnd));
        if (!accepted && shellActivated)
        {
            for (var retry = 0; retry < 10; retry++)
            {
                Thread.Sleep(5);
                after = Desktop();
                if (after.foreground_hwnd == previous.hwnd || after.cursor != expectedCursor) break;
            }
            accepted = after.foreground_hwnd == previous.hwnd;
        }
        onCurrentDesktop = Native.IsWindowOnCurrentVirtualDesktop(previousHwnd);
        var foregroundVerified = accepted && after.foreground_hwnd == previous.hwnd && onCurrentDesktop == true;
        var desktopRestored = desktopSwitched && onCurrentDesktop == true
            && Native.GetWindowDesktopGuid(previousHwnd) == Guid.Parse(previous.virtual_desktop_id!);
        var restored = foregroundVerified || desktopRestored;
        var minimizeFallbackAttempted = false;
        string? minimizedWindowHwnd = null;
        bool? activatingWindowMinimized = null;
        if (!foregroundVerified && minimizeActivatingWindow && actionProcessPath is not null
            && after.foreground_hwnd == expectedCurrent && after.cursor == expectedCursor)
        {
            var activeApp = Native.ForegroundWindowIdentity();
            if (activeApp is not null && activeApp.hwnd == expectedCurrent
                && Native.SameApplicationFamily(actionProcessPath, activeApp.pid)
                && long.TryParse(activeApp.hwnd, NumberStyles.None, CultureInfo.InvariantCulture, out var activeRaw))
            {
                var immediatelyBeforeMinimize = Desktop();
                if (immediatelyBeforeMinimize.foreground_hwnd == expectedCurrent
                    && immediatelyBeforeMinimize.cursor == expectedCursor)
                {
                    minimizeFallbackAttempted = true;
                    minimizedWindowHwnd = activeApp.hwnd;
                    Native.MinimizeWindow((nint)activeRaw);
                    for (var retry = 0; retry < 10; retry++)
                    {
                        after = Desktop();
                        if (after.foreground_hwnd == previous.hwnd || retry == 9) break;
                        Thread.Sleep(5);
                    }
                    activatingWindowMinimized = Native.IsIconic((nint)activeRaw);
                    onCurrentDesktop = Native.IsWindowOnCurrentVirtualDesktop(previousHwnd);
                    foregroundVerified = after.foreground_hwnd == previous.hwnd && onCurrentDesktop == true;
                    restored = restored || foregroundVerified;
                }
            }
        }
        var reason = foregroundVerified ? minimizeFallbackAttempted ? "restored_after_minimize_fallback" : "restored"
            : desktopRestored ? "desktop_restored_foreground_pending" : !accepted ? "set_foreground_denied" :
            onCurrentDesktop != true ? "previous_desktop_not_reactivated" : "foreground_verification_failed";
        return new { attempted = true, restored, desktop_restored = desktopRestored, foreground_verified = foregroundVerified, reason,
            desktop_switched = desktopSwitched, shell_activation_succeeded = shellActivated, switch_to_this_window_called = switchToThisWindowCalled,
            minimize_fallback_attempted = minimizeFallbackAttempted, minimized_activating_window_hwnd = minimizedWindowHwnd,
            activating_window_minimized = activatingWindowMinimized,
            user_input_since_observation = before.last_input_tick != expectedLastInput,
            shell_activation_reason = shellActivationReason, restore_elapsed_ms = restoreTimer.Elapsed.TotalMilliseconds,
            previous_foreground = previous, before, after,
            previous_window_on_current_virtual_desktop = onCurrentDesktop };
    }

    private static object? RestoreAfterAction(JsonElement request, ForegroundWindow? previous, DesktopSnapshot before,
        DesktopSnapshot after, string? actionProcessPath)
    {
        if (!request.TryGetProperty("restore_foreground", out var enabled) || enabled.ValueKind != JsonValueKind.True) return null;
        return RestoreForeground(previous, after.foreground_hwnd, before.last_input_tick, before.cursor, before.session_id,
            before.process_desktop, before.input_desktop, actionProcessPath,
            request.TryGetProperty("minimize_activating_window", out var minimize) && minimize.ValueKind == JsonValueKind.True,
            Desktop());
    }

    private static bool TryValidateForegroundWindow(ForegroundWindow previous, out nint hwnd, out string reason)
    {
        hwnd = 0;
        reason = "previous_window_identity_changed";
        if (!long.TryParse(previous.hwnd, NumberStyles.None, CultureInfo.InvariantCulture, out var raw) || raw <= 0) return false;
        hwnd = (nint)raw;
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
        {
            reason = "previous_window_unavailable";
            return false;
        }
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid != previous.pid || Native.WindowTitle(hwnd) != previous.title || Native.ClassName(hwnd) != previous.class_name)
            return false;
        try
        {
            using var process = Process.GetProcessById(previous.pid);
            if (process.SessionId != previous.session_id || process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) != previous.start_time)
                return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            reason = "previous_process_unavailable";
            return false;
        }
        var desktopId = Native.GetWindowDesktopId(hwnd);
        if (desktopId is null || desktopId != previous.virtual_desktop_id)
        {
            reason = "previous_window_desktop_changed";
            return false;
        }
        return true;
    }

    private static T Pattern<T>(AutomationElement element, AutomationPattern pattern) where T : class
        => element.TryGetCurrentPattern(pattern, out var value) ? (T)value
            : throw new ApiException("pattern_not_supported", $"The element does not support {typeof(T).Name}.");

    private static bool NativeControl(AutomationElement element, nint root, int pid, out nint handle, out string className)
    {
        handle = (nint)(uint)element.Current.NativeWindowHandle;
        className = "";
        if (handle == 0) return false;
        if (!Native.IsWindow(handle) || (handle != root && !Native.IsChild(root, handle)))
            throw new ApiException("window_identity_mismatch", "The UIA native handle escaped the bound window.");
        Native.GetWindowThreadProcessId(handle, out var currentPid);
        if (currentPid != pid) throw new ApiException("element_process_mismatch", "The native control belongs to another process.");
        if (!Native.IsWindowEnabled(handle)) throw new ApiException("element_disabled", "The native control is disabled.");
        className = Native.ClassName(handle);
        return true;
    }

    private static void Send(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Native.SendMessageTimeout(hwnd, message, wParam, lParam, 3, 500, out _) == 0)
            throw new ApiException("message_delivery_failed", "Window message delivery failed or timed out.");
    }

    private static void ConfirmExpectedWindow(JsonElement request, nint hwnd)
    {
        if (Native.GetWindowTextLength(hwnd) > 4096 || Native.WindowTitle(hwnd) != String(request, "expected_title") || Native.ClassName(hwnd) != String(request, "expected_class_name"))
            throw new ApiException("window_identity_mismatch", "The window title or class changed. Inspect again.");
    }

    private static object Click(JsonElement request)
    {
        if (request.TryGetProperty("surface_hwnd", out _)) return ClickSurface(request);
        var bound = Bind(request, true);
        using var process = bound.Process;
        ConfirmExpectedWindow(request, bound.Hwnd);
        if (!Native.IsWindowEnabled(bound.Hwnd)) throw new ApiException("element_disabled", "The target window is disabled.");
        var button = String(request, "button");
        if (button is not ("left" or "right")) throw new ApiException("invalid_arguments", "Button must be left or right.");
        var client = Native.ClientRect(bound.Hwnd);
        var window = Native.WindowRect(bound.Hwnd);
        if (request.GetProperty("expected_client_rect").Deserialize<Rect>() != client || request.GetProperty("expected_window_rect").Deserialize<Rect>() != window)
            throw new ApiException("target_moved", "The target geometry changed. Capture again.");
        var x = Integer(request, "client_x", -1, 0, Math.Min(32767, client.width - 1));
        var y = Integer(request, "client_y", -1, 0, Math.Min(32767, client.height - 1));
        var screenPoint = new Native.Point { X = client.x + x, Y = client.y + y };
        foreach (var node in Walk(Root(bound.Hwnd), 32, 5000))
            if (node.Element.Current.IsPassword && node.Element.Current.BoundingRectangle.Contains(screenPoint.X, screenPoint.Y))
                throw new ApiException("password_control", "A password control occupies this point.");
        // A packaged app may expose an ApplicationFrameHost window while its
        // actual UIA content HWND belongs to the app process. Trust only
        // processes surfaced by this bound root's current UIA subtree, plus
        // their bounded process descendants (for example, an embedded WebView).
        var providerProcessIds = ProviderProcessIds(bound.Hwnd);
        Dictionary<int, int>? processParents = null;
        var target = bound.Hwnd;
        var targetPid = process.Id;
        var targetStart = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        var hitTestRoute = "bound_window";
        for (var depth = 0; depth < 8; ++depth)
        {
            var point = screenPoint;
            if (!Native.ScreenToClient(target, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var child = Native.ChildWindowFromPointEx(target, point, 1 | 2 | 4);
            if (child == 0 || child == target) break;
            if (!Native.IsChild(bound.Hwnd, child)) throw new ApiException("window_identity_mismatch", "Hit-test child escaped the bound window.");
            Native.GetWindowThreadProcessId(child, out var childPid);
            if (childPid == 0) throw new ApiException("window_identity_mismatch", "Hit-test child has no owning process.");
            if (childPid != process.Id)
            {
                if (!Native.IsWindowVisible(child) || !Native.IsWindowEnabled(child))
                    throw new ApiException("element_disabled", "The hit-test child is not visible and enabled.");
                if (providerProcessIds.Contains((int)childPid)) hitTestRoute = "uia_owned_child_process";
                else if (Native.IsProcessDescendant((int)childPid, providerProcessIds, processParents ??= Native.ProcessParentMap()))
                    hitTestRoute = "uia_provider_process_descendant";
                else throw new ApiException("element_process_mismatch", "Hit-test child is outside the bound window's observed UIA/provider process tree.");
                using var childProcess = Process.GetProcessById((int)childPid);
                if (childProcess.SessionId != process.SessionId)
                    throw new ApiException("wrong_session", "Hit-test child belongs to another Windows session.");
                targetStart = childProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                hitTestRoute = "bound_window";
                targetStart = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            }
            targetPid = (int)childPid;
            target = child;
        }
        var targetClient = Native.ClientRect(target);
        var targetWindow = Native.WindowRect(target);
        if (screenPoint.X < targetClient.x || screenPoint.X >= targetClient.x + targetClient.width
            || screenPoint.Y < targetClient.y || screenPoint.Y >= targetClient.y + targetClient.height)
            throw new ApiException("target_moved", "The resolved child does not contain the observed click point.");
        var local = screenPoint;
        if (!Native.ScreenToClient(target, ref local)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (local.X is < 0 or > 32767 || local.Y is < 0 or > 32767) throw new ApiException("invalid_coordinates", "Child coordinates exceed mouse-message range.");
        var foregroundBefore = Native.ForegroundWindowIdentity();
        var before = Desktop();
        var actionProcessPath = Native.ProcessImagePath(process);
        using (var confirmation = Bind(request, true).Process) { }
        ConfirmExpectedWindow(request, bound.Hwnd);
        if (Native.ClientRect(bound.Hwnd) != client || Native.WindowRect(bound.Hwnd) != window)
            throw new ApiException("target_moved", "The target moved before message delivery.");
        if (!Native.IsWindow(target) || (target != bound.Hwnd && !Native.IsChild(bound.Hwnd, target)))
            throw new ApiException("window_identity_mismatch", "The resolved input child is no longer under the bound window.");
        Native.GetWindowThreadProcessId(target, out var currentTargetPid);
        if (currentTargetPid != targetPid) throw new ApiException("window_identity_mismatch", "The resolved input child changed process.");
        if (!Native.IsWindowVisible(target) || !Native.IsWindowEnabled(target)
            || Native.ClientRect(target) != targetClient || Native.WindowRect(target) != targetWindow)
            throw new ApiException("target_moved", "The resolved child visibility or geometry changed before message delivery.");
        using (var currentTargetProcess = Process.GetProcessById(targetPid))
            if (currentTargetProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) != targetStart)
                throw new ApiException("window_identity_mismatch", "The resolved input process changed before message delivery.");
        var lParam = (nint)((local.Y << 16) | (local.X & 0xffff));
        if (button == "left" && Native.IsButton(Native.ClassName(target)))
        {
            Send(target, 0x00F5, 0, 0);
            var actionAfter = Desktop();
            var focusRestoration = RestoreAfterAction(request, foregroundBefore, before, actionAfter, actionProcessPath);
            return new { messages_delivered = 1, target_hwnd = HandleText(target), target_process_id = targetPid, hit_test_route = hitTestRoute, button, client_x = x, client_y = y,
                before, after = Desktop(), action_after = actionAfter, focus_restoration = focusRestoration,
                action_process_path = actionProcessPath,
                minimize_activating_window = request.TryGetProperty("minimize_activating_window", out var minimizeButton) && minimizeButton.ValueKind == JsonValueKind.True,
                foreground_before = foregroundBefore, method = "window-message", delivery_route = "bm_click", semantic_success = (bool?)null };
        }
        var down = button == "left" ? 0x0201u : 0x0204u;
        var up = button == "left" ? 0x0202u : 0x0205u;
        var key = button == "left" ? 1u : 2u;
        var delivered = 0;
        foreach (var message in new[] { (0x0200u, 0u), (down, key), (up, 0u) })
        {
            if (Native.SendMessageTimeout(target, message.Item1, (nuint)message.Item2, lParam, 3, 500, out _) == 0)
                throw new ApiException("message_delivery_failed", $"Window message delivery stopped after {delivered} messages.");
            delivered++;
        }
        var clickAfter = Desktop();
        var clickFocusRestoration = RestoreAfterAction(request, foregroundBefore, before, clickAfter, actionProcessPath);
        return new { messages_delivered = delivered, target_hwnd = HandleText(target), target_process_id = targetPid, hit_test_route = hitTestRoute, button, client_x = x, client_y = y,
            before, after = Desktop(), action_after = clickAfter, focus_restoration = clickFocusRestoration,
            action_process_path = actionProcessPath,
                minimize_activating_window = request.TryGetProperty("minimize_activating_window", out var minimizeSurfaceButton) && minimizeSurfaceButton.ValueKind == JsonValueKind.True,
            foreground_before = foregroundBefore, method = "window-message", delivery_route = "mouse_messages", semantic_success = (bool?)null };
    }

    private static object ClickSurface(JsonElement request)
    {
        var bound = Bind(request, rejectMinimized: false);
        using var process = bound.Process;
        ConfirmExpectedWindow(request, bound.Hwnd);
        var rootWindow = Native.WindowRect(bound.Hwnd);
        var rootClient = Native.ClientRect(bound.Hwnd);
        if (request.GetProperty("expected_window_rect").Deserialize<Rect>() != rootWindow
            || request.GetProperty("expected_client_rect").Deserialize<Rect>() != rootClient)
            throw new ApiException("target_moved", "The bound root geometry changed. Capture again.");
        var surfaceHwnd = Handle(String(request, "surface_hwnd"));
        var surfacePid = Integer(request, "surface_pid", 0, 1, int.MaxValue);
        var surfaceStart = String(request, "surface_start_time");
        var candidates = SurfaceCandidates(bound.Hwnd, process)
            .Where(item => item.Hwnd == surfaceHwnd && item.Pid == surfacePid && item.StartTime == surfaceStart).Take(2).ToArray();
        if (candidates.Length != 1) throw new ApiException("stale_surface", "The child surface identity changed or is no longer visible; observe again.");
        var surface = candidates[0];
        if (request.GetProperty("expected_surface_window_rect").Deserialize<Rect>() != surface.WindowRect
            || request.GetProperty("expected_surface_client_rect").Deserialize<Rect>() != surface.ClientRect)
            throw new ApiException("target_moved", "The child surface geometry changed. Capture again.");
        var button = String(request, "button");
        if (button is not ("left" or "right")) throw new ApiException("invalid_arguments", "Button must be left or right.");
        var screenPoint = new Native.Point
        {
            X = Integer(request, "screen_x", 0, int.MinValue, int.MaxValue),
            Y = Integer(request, "screen_y", 0, int.MinValue, int.MaxValue)
        };
        if (screenPoint.X < surface.ClientRect.x || screenPoint.X >= surface.ClientRect.x + surface.ClientRect.width
            || screenPoint.Y < surface.ClientRect.y || screenPoint.Y >= surface.ClientRect.y + surface.ClientRect.height)
            throw new ApiException("outside_client", "The surface-image pixel is outside its verified client area.");
        if (!Native.IsIconic(bound.Hwnd) && !Contains(rootClient, new Rect(screenPoint.X, screenPoint.Y, 1, 1)))
            throw new ApiException("outside_client", "Child surface clicks are limited to the bound root's visible client area.");
        foreach (var node in Walk(Root(bound.Hwnd), 32, 5000))
            if (node.Element.Current.IsPassword && node.Element.Current.BoundingRectangle.Contains(screenPoint.X, screenPoint.Y))
                throw new ApiException("password_control", "A password control occupies this point.");

        var providerProcessIds = ProviderProcessIds(bound.Hwnd);
        Dictionary<int, int>? processParents = null;
        var target = surface.Hwnd;
        var targetPid = surface.Pid;
        var targetStart = surface.StartTime;
        var hitTestRoute = "surface_" + surface.Route;
        for (var depth = 0; depth < 8; ++depth)
        {
            var point = screenPoint;
            if (!Native.ScreenToClient(target, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var child = Native.ChildWindowFromPointEx(target, point, 1 | 2 | 4);
            if (child == 0 || child == target) break;
            if (!Native.IsChild(surface.Hwnd, child) && !Native.IsChild(bound.Hwnd, child))
                throw new ApiException("window_identity_mismatch", "Hit-test child escaped the bound root/surface.");
            Native.GetWindowThreadProcessId(child, out var childPid);
            if (childPid == 0) throw new ApiException("window_identity_mismatch", "Hit-test child has no owning process.");
            if (childPid != targetPid)
            {
                if (!Native.IsWindowVisible(child) || !Native.IsWindowEnabled(child))
                    throw new ApiException("element_disabled", "The hit-test child is not visible and enabled.");
                if (providerProcessIds.Contains((int)childPid)) hitTestRoute = "uia_owned_child_process";
                else if (Native.IsProcessDescendant((int)childPid, providerProcessIds, processParents ??= Native.ProcessParentMap()))
                    hitTestRoute = "uia_provider_process_descendant";
                else throw new ApiException("element_process_mismatch", "Hit-test child is outside the bound window's observed UIA/provider process tree.");
                using var childProcess = Process.GetProcessById((int)childPid);
                if (childProcess.SessionId != process.SessionId)
                    throw new ApiException("wrong_session", "Hit-test child belongs to another Windows session.");
                targetStart = childProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            }
            else if (childPid == process.Id)
            {
                hitTestRoute = "bound_window";
                targetStart = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            }
            targetPid = (int)childPid;
            target = child;
        }
        var targetClient = Native.ClientRect(target);
        var targetWindow = Native.WindowRect(target);
        if (screenPoint.X < targetClient.x || screenPoint.X >= targetClient.x + targetClient.width
            || screenPoint.Y < targetClient.y || screenPoint.Y >= targetClient.y + targetClient.height)
            throw new ApiException("target_moved", "The resolved child does not contain the observed click point.");
        var local = screenPoint;
        if (!Native.ScreenToClient(target, ref local)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (local.X is < 0 or > 32767 || local.Y is < 0 or > 32767)
            throw new ApiException("invalid_coordinates", "Child coordinates exceed mouse-message range.");
        var foregroundBefore = Native.ForegroundWindowIdentity();
        var before = Desktop();
        var actionProcessPath = Native.ProcessImagePath(process);
        using (var confirmation = Bind(request, rejectMinimized: false).Process) { }
        ConfirmExpectedWindow(request, bound.Hwnd);
        if (Native.WindowRect(bound.Hwnd) != rootWindow || Native.ClientRect(bound.Hwnd) != rootClient)
            throw new ApiException("target_moved", "The bound root moved before message delivery.");
        var surfaceStillValid = SurfaceCandidates(bound.Hwnd, process)
            .Any(item => item.Hwnd == surface.Hwnd && item.Pid == surface.Pid && item.StartTime == surface.StartTime
                && item.WindowRect == surface.WindowRect && item.ClientRect == surface.ClientRect);
        var targetRelated = target == surface.Hwnd || Native.IsChild(surface.Hwnd, target) || Native.IsChild(bound.Hwnd, target);
        if (!surfaceStillValid || !Native.IsWindow(target) || !targetRelated
            || !Native.IsWindowVisible(target) || !Native.IsWindowEnabled(target)
            || Native.ClientRect(surface.Hwnd) != surface.ClientRect || Native.WindowRect(surface.Hwnd) != surface.WindowRect
            || Native.ClientRect(target) != targetClient || Native.WindowRect(target) != targetWindow)
            throw new ApiException("target_moved", "The resolved surface/child relationship or geometry changed before message delivery.");
        Native.GetWindowThreadProcessId(target, out var currentTargetPid);
        if (currentTargetPid != targetPid) throw new ApiException("window_identity_mismatch", "The resolved input child changed process.");
        Native.GetWindowThreadProcessId(surface.Hwnd, out var currentSurfacePid);
        if (currentSurfacePid != surface.Pid) throw new ApiException("window_identity_mismatch", "The selected child surface changed process.");
        using (var currentTargetProcess = Process.GetProcessById(targetPid))
            if (currentTargetProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) != targetStart)
                throw new ApiException("window_identity_mismatch", "The resolved input process changed before message delivery.");
        using (var currentSurfaceProcess = Process.GetProcessById(surface.Pid))
            if (currentSurfaceProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) != surface.StartTime)
                throw new ApiException("window_identity_mismatch", "The child surface process changed before message delivery.");
        var lParam = (nint)((local.Y << 16) | (local.X & 0xffff));
        if (button == "left" && Native.IsButton(Native.ClassName(target)))
        {
            Send(target, 0x00F5, 0, 0);
            var actionAfter = Desktop();
            var focusRestoration = RestoreAfterAction(request, foregroundBefore, before, actionAfter, actionProcessPath);
            return new { messages_delivered = 1, target_hwnd = HandleText(target), target_process_id = targetPid,
                hit_test_route = hitTestRoute, input_surface_hwnd = HandleText(surface.Hwnd), button, screen_x = screenPoint.X, screen_y = screenPoint.Y,
                before, after = Desktop(), action_after = actionAfter, focus_restoration = focusRestoration,
                action_process_path = actionProcessPath,
                minimize_activating_window = request.TryGetProperty("minimize_activating_window", out var minimizeButton) && minimizeButton.ValueKind == JsonValueKind.True,
                foreground_before = foregroundBefore, method = "window-message", delivery_route = "bm_click", semantic_success = (bool?)null };
        }
        var down = button == "left" ? 0x0201u : 0x0204u;
        var up = button == "left" ? 0x0202u : 0x0205u;
        var key = button == "left" ? 1u : 2u;
        var delivered = 0;
        foreach (var message in new[] { (0x0200u, 0u), (down, key), (up, 0u) })
        {
            if (Native.SendMessageTimeout(target, message.Item1, (nuint)message.Item2, lParam, 3, 500, out _) == 0)
                throw new ApiException("message_delivery_failed", $"Window message delivery stopped after {delivered} messages.");
            delivered++;
        }
        var clickAfter = Desktop();
        var clickFocusRestoration = RestoreAfterAction(request, foregroundBefore, before, clickAfter, actionProcessPath);
        return new { messages_delivered = delivered, target_hwnd = HandleText(target), target_process_id = targetPid,
            hit_test_route = hitTestRoute, input_surface_hwnd = HandleText(surface.Hwnd), button, screen_x = screenPoint.X, screen_y = screenPoint.Y,
            before, after = Desktop(), action_after = clickAfter, focus_restoration = clickFocusRestoration,
            action_process_path = actionProcessPath,
            minimize_activating_window = request.TryGetProperty("minimize_activating_window", out var minimizeSurfaceAction) && minimizeSurfaceAction.ValueKind == JsonValueKind.True,
            foreground_before = foregroundBefore, method = "window-message", delivery_route = "mouse_messages", semantic_success = (bool?)null };
    }

    private static HashSet<int> ProviderProcessIds(nint root)
    {
        return ProviderSurfaceHandles(root).Values.ToHashSet();
    }

    private static Dictionary<nint, int> ProviderSurfaceHandles(nint root)
    {
        var handles = new Dictionary<nint, int>();
        foreach (var node in Walk(Root(root), 32, 5000))
        {
            var hwnd = (nint)(uint)node.Element.Current.NativeWindowHandle;
            if (hwnd == 0 || !Native.IsWindow(hwnd)) continue;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0) handles[hwnd] = (int)pid;
        }
        return handles;
    }

    private static bool Contains(Rect outer, Rect inner)
        => inner.x >= outer.x && inner.y >= outer.y
            && (long)inner.x + inner.width <= (long)outer.x + outer.width
            && (long)inner.y + inner.height <= (long)outer.y + outer.height;

    private static bool Intersects(Rect left, Rect right)
        => left.width > 0 && left.height > 0 && right.width > 0 && right.height > 0
            && left.x < (long)right.x + right.width && right.x < (long)left.x + left.width
            && left.y < (long)right.y + right.height && right.y < (long)left.y + left.height;

    private static WindowSurface[] SurfaceCandidates(nint root, Process rootProcess)
    {
        var rootClient = Native.ClientRect(root);
        var rootMinimized = Native.IsIconic(root);
        var providerHandles = ProviderSurfaceHandles(root);
        var providerProcessIds = providerHandles.Values.ToHashSet();
        var providerRects = new List<Rect>();
        foreach (var handle in providerHandles.Keys)
        {
            try
            {
                if (Native.IsWindowVisible(handle) && !Native.IsIconic(handle)) providerRects.Add(Native.WindowRect(handle));
            }
            catch (Win32Exception) { }
        }
        Dictionary<int, int>? processParents = null;
        var candidates = new List<WindowSurface>();
        var seen = new HashSet<nint>();
        void AddCandidate(nint hwnd)
        {
            try
            {
                if (hwnd == root || !seen.Add(hwnd) || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return;
                var windowRect = Native.WindowRect(hwnd);
                if (windowRect.width < 64 || windowRect.height < 48) return;
                Native.GetWindowThreadProcessId(hwnd, out var rawPid);
                if (rawPid == 0) return;
                var pid = (int)rawPid;
                string route;
                var uiaSurface = providerHandles.TryGetValue(hwnd, out var surfacedPid) && surfacedPid == pid;
                var childOfRoot = Native.IsChild(root, hwnd);
                if (uiaSurface) route = "uia_provider_surface";
                else if (pid == rootProcess.Id)
                {
                    if (!childOfRoot) return;
                    route = "same_process_child";
                }
                else if (providerProcessIds.Contains(pid)) route = "uia_provider_process";
                else
                {
                    processParents ??= Native.ProcessParentMap();
                    if (!Native.IsProcessDescendant(pid, providerProcessIds, processParents)) return;
                    route = "uia_provider_process_descendant";
                }
                if (!rootMinimized && !childOfRoot
                    && !Intersects(rootClient, windowRect) && !providerRects.Any(rect => Intersects(rect, windowRect))) return;
                if (rootMinimized && !childOfRoot
                    && !((uiaSurface && pid != rootProcess.Id) || providerRects.Any(rect => Intersects(rect, windowRect)))) return;
                using var owner = Process.GetProcessById(pid);
                if (owner.SessionId != rootProcess.SessionId) return;
                var startTime = owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                candidates.Add(new WindowSurface(hwnd, pid, startTime, Native.WindowTitle(hwnd), Native.ClassName(hwnd),
                    windowRect, Native.ClientRect(hwnd), route));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or ApiException)
            {
                // A disappearing or inaccessible child is omitted; it can never expand the bound window scope.
            }
        }

        Native.EnumChildWindows(root, (hwnd, _) => { AddCandidate(hwnd); return candidates.Count < 128; }, 0);
        foreach (var handle in providerHandles.Keys)
        {
            if (candidates.Count >= 128) break;
            AddCandidate(handle);
            Native.EnumChildWindows(handle, (hwnd, _) => { AddCandidate(hwnd); return candidates.Count < 128; }, 0);
        }
        Native.EnumWindows((hwnd, _) =>
        {
            if (candidates.Count >= 128) return false;
            AddCandidate(hwnd);
            if (candidates.Any(item => item.Hwnd == hwnd))
                Native.EnumChildWindows(hwnd, (child, _) => { AddCandidate(child); return candidates.Count < 128; }, 0);
            return candidates.Count < 128;
        }, 0);
        return candidates.OrderByDescending(item => (long)item.WindowRect.width * item.WindowRect.height).Take(12).ToArray();
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct WinRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct LastInput { public uint Size; public uint Tick; }
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(nint topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(nint topLevelWindow, in Guid desktopId);
    }
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderCom
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
    [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManagerInternal
    {
        int GetCount();
        void MoveViewToDesktop([MarshalAs(UnmanagedType.IUnknown)] object view, [MarshalAs(UnmanagedType.IUnknown)] object desktop);
        [return: MarshalAs(UnmanagedType.Bool)] bool CanViewMoveDesktops([MarshalAs(UnmanagedType.IUnknown)] object view);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentDesktop();
        void GetDesktops([MarshalAs(UnmanagedType.Interface)] out IObjectArrayInternal desktops);
        [PreserveSig] int GetAdjacentDesktop([MarshalAs(UnmanagedType.IUnknown)] object from, int direction,
            [MarshalAs(UnmanagedType.IUnknown)] out object desktop);
        void SwitchDesktop([MarshalAs(UnmanagedType.IUnknown)] object desktop);
    }
    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollectionCom
    {
        [PreserveSig] int GetViews([MarshalAs(UnmanagedType.Interface)] out IObjectArrayInternal views);
        [PreserveSig] int GetViewsByZOrder([MarshalAs(UnmanagedType.Interface)] out IObjectArrayInternal views);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.Interface)] out IObjectArrayInternal views);
        [PreserveSig] int GetViewForHwnd(nint hwnd, [MarshalAs(UnmanagedType.Interface)] out IApplicationViewCom view);
    }
    [ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCom
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int SwitchTo();
    }
    [ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopInternal
    {
        [return: MarshalAs(UnmanagedType.Bool)] bool IsViewVisible([MarshalAs(UnmanagedType.IUnknown)] object view);
        Guid GetId();
    }
    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArrayInternal
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
    [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"), ClassInterface(ClassInterfaceType.None)]
    private class VirtualDesktopManagerCom { }
    [ComImport, Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239"), ClassInterface(ClassInterfaceType.None)]
    private class ImmersiveShellCom { }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableName;
    }
    internal delegate bool EnumCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent, EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SwitchToThisWindow")] internal static extern void SwitchToThisWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool altTabSemantics);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint hwnd, out WinRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint hwnd, out WinRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern nint ChildWindowFromPointEx(nint parent, Point point, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)] private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)] internal static extern nint SendMessageTimeoutText(nint hwnd, uint message, nuint wParam, string text, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out WinRect value, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] internal static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder text, uint length, out uint needed);

    internal static ForegroundWindow? ForegroundWindowIdentity()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || !IsWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var rawPid);
        if (rawPid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)rawPid);
            return new ForegroundWindow(((long)hwnd).ToString(CultureInfo.InvariantCulture), process.Id, process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                WindowTitle(hwnd), ClassName(hwnd), process.SessionId, GetWindowDesktopId(hwnd), IsWindowOnCurrentVirtualDesktop(hwnd));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception)
        {
            return null;
        }
    }

    internal static string? ProcessImagePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    internal static bool SameApplicationFamily(string targetProcessPath, int foregroundPid)
    {
        try
        {
            using var foreground = Process.GetProcessById(foregroundPid);
            if (foreground.SessionId != Process.GetCurrentProcess().SessionId) return false;
            var foregroundPath = ProcessImagePath(foreground);
            if (foregroundPath is null) return false;
            var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetProcessPath));
            var foregroundDirectory = Path.GetDirectoryName(Path.GetFullPath(foregroundPath));
            if (!string.Equals(targetDirectory, foregroundDirectory, StringComparison.OrdinalIgnoreCase)) return false;
            var targetName = Path.GetFileNameWithoutExtension(targetProcessPath);
            var foregroundName = Path.GetFileNameWithoutExtension(foregroundPath);
            return string.Equals(targetName, foregroundName, StringComparison.OrdinalIgnoreCase)
                || foregroundName.StartsWith(targetName, StringComparison.OrdinalIgnoreCase)
                || targetName.StartsWith(foregroundName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool MinimizeWindow(nint hwnd)
        => IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd) && ShowWindowAsync(hwnd, 6);

    internal static string? GetWindowDesktopId(nint hwnd)
    {
        var id = GetWindowDesktopGuid(hwnd);
        return id?.ToString("D");
    }

    internal static Guid? GetWindowDesktopGuid(nint hwnd)
    {
        var manager = CreateVirtualDesktopManager();
        if (manager is null) return null;
        try { return manager.GetWindowDesktopId(hwnd, out var id) == 0 ? id : null; }
        catch (COMException) { return null; }
        finally { if (Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager); }
    }

    internal static bool TrySwitchVirtualDesktop(Guid targetDesktop, out string reason)
    {
        reason = "virtual_desktop_switch_unavailable";
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) { reason = "virtual_desktop_switch_unsupported_build"; return false; }
        object? shellObject = null;
        object? managerObject = null;
        IObjectArrayInternal? desktops = null;
        object? matchedDesktop = null;
        try
        {
            shellObject = Activator.CreateInstance(typeof(ImmersiveShellCom));
            if (shellObject is not IServiceProviderCom shell) return false;
            var serviceId = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
            var interfaceId = new Guid("53F5CA0B-158F-4124-900C-057158060B27");
            var hresult = shell.QueryService(ref serviceId, ref interfaceId, out managerObject);
            if (hresult < 0 || managerObject is not IVirtualDesktopManagerInternal manager) return false;
            manager.GetDesktops(out desktops);
            desktops.GetCount(out var count);
            if (count is < 1 or > 128) return false;
            var desktopInterfaceId = new Guid("3F07F4BE-B107-441A-AF0F-39D82529072C");
            for (var index = 0; index < count; index++)
            {
                desktops.GetAt(index, ref desktopInterfaceId, out matchedDesktop);
                if (matchedDesktop is IVirtualDesktopInternal desktop && desktop.GetId() == targetDesktop)
                {
                    manager.SwitchDesktop(matchedDesktop);
                    reason = "switch_requested";
                    return true;
                }
                if (matchedDesktop is not null && Marshal.IsComObject(matchedDesktop)) Marshal.FinalReleaseComObject(matchedDesktop);
                matchedDesktop = null;
            }
            reason = "previous_virtual_desktop_missing";
            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            reason = "virtual_desktop_switch_unavailable";
            return false;
        }
        finally
        {
            if (matchedDesktop is not null && Marshal.IsComObject(matchedDesktop)) Marshal.FinalReleaseComObject(matchedDesktop);
            if (desktops is not null && Marshal.IsComObject(desktops)) Marshal.FinalReleaseComObject(desktops);
            if (managerObject is not null && Marshal.IsComObject(managerObject)) Marshal.FinalReleaseComObject(managerObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject)) Marshal.FinalReleaseComObject(shellObject);
        }
    }

    internal static bool TrySwitchToApplicationView(nint hwnd, out string reason)
    {
        reason = "shell_view_activation_unavailable";
        object? shellObject = null;
        object? collectionObject = null;
        IApplicationViewCom? view = null;
        try
        {
            shellObject = Activator.CreateInstance(typeof(ImmersiveShellCom));
            if (shellObject is not IServiceProviderCom shell) return false;
            var serviceId = new Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
            var interfaceId = serviceId;
            var hresult = shell.QueryService(ref serviceId, ref interfaceId, out collectionObject);
            if (hresult < 0 || collectionObject is not IApplicationViewCollectionCom collection) return false;
            hresult = collection.GetViewForHwnd(hwnd, out view);
            if (hresult < 0 || view is null) { reason = "shell_view_not_found"; return false; }
            hresult = view.SwitchTo();
            if (hresult < 0) { reason = "shell_view_activation_denied"; return false; }
            reason = "shell_view_switch_requested";
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            reason = "shell_view_activation_unavailable";
            return false;
        }
        finally
        {
            if (view is not null && Marshal.IsComObject(view)) Marshal.FinalReleaseComObject(view);
            if (collectionObject is not null && Marshal.IsComObject(collectionObject)) Marshal.FinalReleaseComObject(collectionObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject)) Marshal.FinalReleaseComObject(shellObject);
        }
    }

    internal static bool? IsWindowOnCurrentVirtualDesktop(nint hwnd)
    {
        var manager = CreateVirtualDesktopManager();
        if (manager is null) return null;
        try { return manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var current) == 0 ? current : null; }
        catch (COMException) { return null; }
        finally { if (Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager); }
    }

    private static IVirtualDesktopManager? CreateVirtualDesktopManager()
    {
        try { return Activator.CreateInstance(typeof(VirtualDesktopManagerCom)) as IVirtualDesktopManager; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }

    internal static Dictionary<int, int> ProcessParentMap()
    {
        const uint snapshotProcesses = 0x00000002;
        var snapshot = CreateToolhelp32Snapshot(snapshotProcesses, 0);
        if (snapshot == 0 || snapshot == (nint)(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new Dictionary<int, int>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>(), ExecutableName = string.Empty };
            if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally { CloseHandle(snapshot); }
    }

    internal static bool IsProcessDescendant(int processId, IReadOnlySet<int> trustedAncestors, IReadOnlyDictionary<int, int> parentMap)
    {
        var seen = new HashSet<int> { processId };
        var current = processId;
        for (var depth = 0; depth < 16 && parentMap.TryGetValue(current, out var parent); ++depth)
        {
            if (parent <= 0 || !seen.Add(parent)) break;
            if (trustedAncestors.Contains(parent)) return true;
            current = parent;
        }
        return false;
    }

    internal static string WindowTitle(nint hwnd) { var text = new StringBuilder(4097); GetWindowText(hwnd, text, text.Capacity); return text.ToString(); }
    internal static string ClassName(nint hwnd) { var text = new StringBuilder(512); GetClassName(hwnd, text, text.Capacity); return text.ToString(); }
    internal static bool IsButton(string value) => value.Equals("Button", StringComparison.OrdinalIgnoreCase) || value.StartsWith("WindowsForms10.BUTTON.", StringComparison.OrdinalIgnoreCase);
    internal static bool IsEdit(string value) => value.Equals("Edit", StringComparison.OrdinalIgnoreCase) || value.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase) || value.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
    internal static string DesktopName(nint handle)
    {
        var text = new StringBuilder(512);
        return handle != 0 && GetUserObjectInformation(handle, 2, text, (uint)text.Capacity * 2, out _) ? text.ToString() : "unavailable";
    }
    internal static Rect WindowRect(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    internal static Rect ClientRect(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var origin = new Point { X = rect.Left, Y = rect.Top };
        if (!ClientToScreen(hwnd, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    internal static Rect? ExtendedRect(nint hwnd)
        => DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<WinRect>()) == 0
            ? new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : null;
}
