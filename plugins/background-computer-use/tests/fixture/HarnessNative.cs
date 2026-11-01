using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace BackgroundComputerUseFixtureHarness
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION
        { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder value, int maximum);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendText(IntPtr hwnd, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr ReadText(IntPtr hwnd, uint message, UIntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendNumber(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        public static IntPtr ParseHandle(string value) { return new IntPtr(Convert.ToInt64(value.Substring(2), 16)); }
        public static string Hex(IntPtr value) { return "0x" + value.ToInt64().ToString("X"); }
        public static int StartHiddenNoInheritedHandles(string executable, string arguments)
        {
            if (executable.IndexOf('"') >= 0) throw new ArgumentException("Unsupported executable quote.");
            var startup = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), dwFlags = 1, wShowWindow = 0 };
            PROCESS_INFORMATION process;
            // CREATE_NO_WINDOW, SW_HIDE, and no inherited handles: caller's captured stdio pipes cannot leak into the fixture.
            if (!CreateProcessW(executable, new StringBuilder("\"" + executable + "\" " + arguments), IntPtr.Zero,
                IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref startup, out process))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { return process.dwProcessId; }
            finally { CloseHandle(process.hThread); CloseHandle(process.hProcess); }
        }
        public static void UsePhysicalPixels()
        {
            if (SetThreadDpiAwarenessContext(new IntPtr(-4)) == IntPtr.Zero) throw new InvalidOperationException("Cannot enable physical pixel coordinates on the harness thread.");
        }
        public static Dictionary<string, object> Snapshot()
        {
            UsePhysicalPixels();
            POINT point;
            if (!GetCursorPos(out point)) throw new InvalidOperationException("GetCursorPos failed.");
            var input = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref input)) throw new InvalidOperationException("GetLastInputInfo failed.");
            IntPtr foreground = GetForegroundWindow();
            uint foregroundPid;
            GetWindowThreadProcessId(foreground, out foregroundPid);
            return new Dictionary<string, object> {
                { "utc", DateTime.UtcNow.ToString("o") }, { "foregroundHwnd", Hex(foreground) }, { "foregroundPid", foregroundPid },
                { "cursorX", point.X }, { "cursorY", point.Y }, { "lastInputTick", input.dwTime }
            };
        }
        public static void RequireOwnedWindow(string handle, int expectedPid)
        {
            IntPtr hwnd = ParseHandle(handle);
            uint pid;
            if (!IsWindow(hwnd) || GetWindowThreadProcessId(hwnd, out pid) == 0 || pid != (uint)expectedPid)
                throw new InvalidOperationException("Refusing access: handle does not belong to the verified fixture process.");
        }
        public static Dictionary<string, object> InspectWindow(string handle, int expectedPid)
        {
            RequireOwnedWindow(handle, expectedPid);
            IntPtr hwnd = ParseHandle(handle);
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) throw new InvalidOperationException("GetWindowRect failed.");
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            return new Dictionary<string, object> {
                { "hwnd", handle }, { "visible", IsWindowVisible(hwnd) }, { "className", className.ToString() },
                { "minimized", IsIconic(hwnd) },
                { "noActivate", (GetWindowLong(hwnd, -20) & 0x08000000) != 0 }, { "ownerHwnd", Hex(GetWindow(hwnd, 4)) },
                { "x", rect.Left }, { "y", rect.Top }, { "width", rect.Right - rect.Left }, { "height", rect.Bottom - rect.Top }
            };
        }
        public static void SetText(string handle, int expectedPid, string value)
        {
            RequireOwnedWindow(handle, expectedPid);
            UIntPtr result;
            if (SendText(ParseHandle(handle), 0x000C, UIntPtr.Zero, value, 3, 1000, out result) == IntPtr.Zero || result == UIntPtr.Zero)
                throw new InvalidOperationException("Fixture WM_SETTEXT failed or timed out.");
        }
        public static void Click(string handle, int expectedPid)
        {
            RequireOwnedWindow(handle, expectedPid);
            UIntPtr result;
            if (SendNumber(ParseHandle(handle), 0x00F5, UIntPtr.Zero, IntPtr.Zero, 3, 1000, out result) == IntPtr.Zero)
                throw new InvalidOperationException("Fixture BM_CLICK failed or timed out.");
        }
        public static string Text(string handle, int expectedPid)
        {
            RequireOwnedWindow(handle, expectedPid);
            UIntPtr result;
            var value = new StringBuilder(4096);
            if (ReadText(ParseHandle(handle), 0x000D, new UIntPtr((uint)value.Capacity), value, 3, 1000, out result) == IntPtr.Zero)
                throw new InvalidOperationException("Fixture WM_GETTEXT failed or timed out.");
            return value.ToString();
        }
        public static Dictionary<string, object> Capture(string handle, int expectedPid, string path)
        {
            RequireOwnedWindow(handle, expectedPid);
            IntPtr hwnd = ParseHandle(handle);
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) throw new InvalidOperationException("GetWindowRect failed.");
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || width > 1200 || height > 800) throw new InvalidOperationException("Unexpected fixture dimensions.");
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                bool printed;
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Magenta);
                    IntPtr dc = graphics.GetHdc();
                    try { printed = PrintWindow(hwnd, dc, 2); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                bitmap.Save(path, ImageFormat.Png);
                int targetPixels = 0, coverPixels = 0, stripePixels = 0, counterPixels = 0;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    Color color = bitmap.GetPixel(x, y);
                    int rgb = color.ToArgb() & 0x00FFFFFF;
                    if (rgb == 0x249CB7) targetPixels++;
                    if (rgb == 0x263445) coverPixels++;
                    if (rgb == 0xE5883A) stripePixels++;
                    if (rgb == 0xFFC44A) counterPixels++;
                }
                return new Dictionary<string, object> {
                    { "printed", printed }, { "width", width }, { "height", height }, { "path", path },
                    { "targetBackgroundPixels", targetPixels }, { "coverBackgroundPixels", coverPixels },
                    { "coverStripePixels", stripePixels }, { "counterBackgroundPixels", counterPixels }
                };
            }
        }
        public static Dictionary<string, object> ImageStatistics(string path)
        {
            using (var bitmap = new Bitmap(path))
            {
                if (bitmap.Width > 1200 || bitmap.Height > 800) throw new InvalidOperationException("Unexpected fixture image dimensions.");
                int targetPixels = 0, coverPixels = 0, stripePixels = 0, counterPixels = 0;
                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                {
                    int rgb = bitmap.GetPixel(x, y).ToArgb() & 0x00FFFFFF;
                    if (rgb == 0x249CB7) targetPixels++;
                    if (rgb == 0x263445) coverPixels++;
                    if (rgb == 0xE5883A) stripePixels++;
                    if (rgb == 0xFFC44A) counterPixels++;
                }
                return new Dictionary<string, object> {
                    { "width", bitmap.Width }, { "height", bitmap.Height }, { "targetBackgroundPixels", targetPixels },
                    { "coverBackgroundPixels", coverPixels }, { "coverStripePixels", stripePixels }, { "counterBackgroundPixels", counterPixels }
                };
            }
        }
    }
}
