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
        return new { name = "background-computer-use-native", version = "1.0.0", platform = "windows", architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            wgc_supported = services.GetRequiredService<IWindowCapture>().IsFrameCaptureSupported,
            uia_backend = "Windows UIAutomationClient", capture_backend = "Microsoft WinApp UIAutomation 0.7.1 WGC-only",
            message_click_supported = true, desktop = Desktop(false) };
    }

    private static ServiceProvider Services()
        => new ServiceCollection().AddLogging().AddWinAppUiAutomation().BuildServiceProvider();

    private static object Desktop(bool requireInteractive = true)
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
        return new { foreground_hwnd = HandleText(Native.GetForegroundWindow()), cursor = new Cursor(position.X, position.Y), last_input_tick = lastInput,
            session_id = Process.GetCurrentProcess().SessionId, process_desktop = processDesktop,
            input_desktop = inputDesktop, interactive };
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
        var after = Desktop();
        return new { window = Window(bound.Hwnd, process), elements, truncated, desktop = after, before, after };
    }

    private static async Task<object> Capture(JsonElement request)
    {
        var bound = Bind(request, true);
        using var process = bound.Process;
        var path = Path.GetFullPath(String(request, "path"));
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ApiException("invalid_arguments", "Capture output must be a PNG path.");
        if (File.Exists(path)) throw new ApiException("output_exists", "Capture never overwrites an existing file.");
        var timeout = Integer(request, "timeout_ms", 3000, 100, 5000);
        var before = Desktop();
        var windowRect = Native.WindowRect(bound.Hwnd);
        var clientRect = Native.ClientRect(bound.Hwnd);
        var extendedRect = Native.ExtendedRect(bound.Hwnd);
        if ((long)windowRect.width * windowRect.height > MaxPixels) throw new ApiException("capture_too_large", "Capture exceeds 20 megapixels.");
        using var services = Services();
        var capture = services.GetRequiredService<IWindowCapture>();
        if (!capture.IsFrameCaptureSupported) throw new ApiException("wgc_unavailable", "WGC is unavailable. Background capture has no fallback.");
        using var cancellation = new CancellationTokenSource(timeout);
        using var grabber = capture.StartFrameGrabber(bound.Hwnd, fps: 10);
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
        using (var confirmation = Bind(request, true).Process) { }
        if (Native.WindowRect(bound.Hwnd) != windowRect || Native.ClientRect(bound.Hwnd) != clientRect)
            throw new ApiException("target_moved", "Window geometry changed during capture. Capture again.");
        Rect? screenRect = extendedRect is Rect extended && extended.width == pixels.Width && extended.height == pixels.Height ? extended
            : windowRect.width == pixels.Width && windowRect.height == pixels.Height ? windowRect : null;
        var image = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Pixels, pixels.Width * 4);
        image.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
        var after = Desktop();
        return new { path, width = pixels.Width, height = pixels.Height, method = "wgc-only", before, after,
            geometry = new { coordinate_space = "physical-screen-pixels", window_rect = windowRect, client_rect = clientRect,
                extended_frame_rect = extendedRect, frame_rect = new Rect(0, 0, pixels.Width, pixels.Height),
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
        var before = Desktop();
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
        return new { performed = true, action, pattern, delivery_route = deliveryRoute, before, after = Desktop(), hwnd = HandleText(bound.Hwnd), runtime_id = runtimeId };
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
        var target = bound.Hwnd;
        for (var depth = 0; depth < 8; ++depth)
        {
            var point = screenPoint;
            if (!Native.ScreenToClient(target, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var child = Native.ChildWindowFromPointEx(target, point, 1 | 2 | 4);
            if (child == 0 || child == target) break;
            if (!Native.IsChild(bound.Hwnd, child)) throw new ApiException("window_identity_mismatch", "Hit-test child escaped the bound window.");
            Native.GetWindowThreadProcessId(child, out var childPid);
            if (childPid != process.Id) throw new ApiException("element_process_mismatch", "Hit-test child belongs to a different process.");
            target = child;
        }
        var local = screenPoint;
        if (!Native.ScreenToClient(target, ref local)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (local.X is < 0 or > 32767 || local.Y is < 0 or > 32767) throw new ApiException("invalid_coordinates", "Child coordinates exceed mouse-message range.");
        var before = Desktop();
        using (var confirmation = Bind(request, true).Process) { }
        ConfirmExpectedWindow(request, bound.Hwnd);
        if (Native.ClientRect(bound.Hwnd) != client || Native.WindowRect(bound.Hwnd) != window)
            throw new ApiException("target_moved", "The target moved before message delivery.");
        var lParam = (nint)((local.Y << 16) | (local.X & 0xffff));
        if (button == "left" && Native.IsButton(Native.ClassName(target)))
        {
            Send(target, 0x00F5, 0, 0);
            return new { messages_delivered = 1, target_hwnd = HandleText(target), button, client_x = x, client_y = y,
                before, after = Desktop(), method = "window-message", delivery_route = "bm_click", semantic_success = (bool?)null };
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
        return new { messages_delivered = delivered, target_hwnd = HandleText(target), button, client_x = x, client_y = y,
            before, after = Desktop(), method = "window-message", delivery_route = "mouse_messages", semantic_success = (bool?)null };
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct WinRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct LastInput { public uint Size; public uint Tick; }
    internal delegate bool EnumCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
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
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)] internal static extern nint SendMessageTimeoutText(nint hwnd, uint message, nuint wParam, string text, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out WinRect value, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] internal static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder text, uint length, out uint needed);
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
