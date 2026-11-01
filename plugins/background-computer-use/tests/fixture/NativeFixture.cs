using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace BackgroundComputerUseFixture
{
    // Deliberately exposes only ordinary, fixture-owned Win32/WinForms controls.
    public sealed class PassiveForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return value;
            }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref message);
        }
    }

    public static class Program
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLong64(IntPtr hwnd, int index, IntPtr value);
        private static void SetControlId(IntPtr hwnd, int value)
        {
            if (IntPtr.Size == 8) SetWindowLong64(hwnd, -12, new IntPtr(value));
            else SetWindowLong32(hwnd, -12, value);
        }
        private static Dictionary<string, object> RectValue(IntPtr hwnd)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) throw new InvalidOperationException("GetWindowRect failed for fixture window.");
            return new Dictionary<string, object> {
                { "x", rect.Left }, { "y", rect.Top }, { "width", rect.Right - rect.Left }, { "height", rect.Bottom - rect.Top }
            };
        }
        private static string Hwnd(IntPtr hwnd) { return "0x" + hwnd.ToInt64().ToString("X"); }

        [STAThread]
        public static void Run(string runDirectory, string instanceId, int lifetimeSeconds)
        {
            if (SetThreadDpiAwarenessContext(new IntPtr(-4)) == IntPtr.Zero) throw new InvalidOperationException("Cannot enable per-monitor physical pixel coordinates on the fixture UI thread.");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string readyPath = Path.Combine(runDirectory, "ready.json");
            string stopPath = Path.Combine(runDirectory, "stop.request");
            string minimizePath = Path.Combine(runDirectory, "minimize.request");
            DateTime startedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            DateTime expiresUtc = DateTime.UtcNow.AddSeconds(lifetimeSeconds);
            int counter = 0;
            string lastEvent = "ready";
            int eventSequence = 0;
            var target = new PassiveForm();
            target.Text = "Background computer use test target " + instanceId;
            target.Name = "BackgroundComputerUseTarget";
            target.AccessibleName = "Background computer use test target";
            target.StartPosition = FormStartPosition.Manual;
            target.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            target.ShowInTaskbar = false;
            target.AutoScaleMode = AutoScaleMode.None;
            target.MaximizeBox = false;
            target.MinimizeBox = false;
            target.ClientSize = new Size(400, 160);
            target.BackColor = Color.FromArgb(36, 156, 183); // #249CB7
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            target.Location = new Point(Math.Max(work.Left, work.Right - target.Width - 24), Math.Max(work.Top, work.Bottom - target.Height - 24));

            var label = new Label { Text = "Test value", Location = new Point(16, 18), Size = new Size(90, 23), AccessibleName = "Test value label" };
            var textbox = new TextBox { Name = "TestValue", AccessibleName = "Test value", Text = "fixture-initial", Location = new Point(112, 16), Size = new Size(270, 24), TabIndex = 0 };
            var increment = new Button { Name = "Increment", AccessibleName = "Increment", Text = "Increment", Location = new Point(16, 62), Size = new Size(120, 32), TabIndex = 1 };
            var counterLabel = new Label { Name = "Counter", AccessibleName = "Counter", Text = "Counter: 0", Location = new Point(152, 69), Size = new Size(220, 26), BackColor = Color.FromArgb(255, 196, 74), ForeColor = Color.Black };
            var notice = new Label { Text = "Owned, non-activating plugin test fixture", Location = new Point(16, 119), Size = new Size(365, 22), AccessibleName = "Fixture notice" };
            target.Controls.AddRange(new Control[] { label, textbox, increment, counterLabel, notice });

            var cover = new PassiveForm();
            cover.Text = "Background computer use fixture cover " + instanceId;
            cover.Name = "BackgroundComputerUseCover";
            cover.AccessibleName = "Background computer use fixture cover";
            cover.StartPosition = FormStartPosition.Manual;
            cover.FormBorderStyle = FormBorderStyle.None;
            cover.ShowInTaskbar = false;
            cover.AutoScaleMode = AutoScaleMode.None;
            cover.BackColor = Color.FromArgb(38, 52, 69); // #263445
            cover.Paint += delegate(object sender, PaintEventArgs args) {
                using (var stripe = new SolidBrush(Color.FromArgb(229, 136, 58))) args.Graphics.FillRectangle(stripe, 0, 0, cover.ClientSize.Width, 12);
                using (var white = new SolidBrush(Color.White)) {
                    args.Graphics.DrawString("Background fixture", SystemFonts.CaptionFont, white, 18, 38);
                    args.Graphics.DrawString("The test target is fully covered.", SystemFonts.MessageBoxFont, white, 18, 72);
                    args.Graphics.DrawString("Automatic expiry closes both windows.", SystemFonts.MessageBoxFont, white, 18, 104);
                }
            };

            Action publish = delegate {
                var value = new Dictionary<string, object> {
                    { "schemaVersion", 1 }, { "instanceId", instanceId }, { "status", "ready" },
                    { "coordinateSpace", "physical-screen-pixels" },
                    { "pid", Process.GetCurrentProcess().Id }, { "processStartedUtc", startedUtc.ToString("o") },
                    { "processStartedUtcTicks", startedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "processCreationFileTimeUtc", startedUtc.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "updatedUtc", DateTime.UtcNow.ToString("o") }, { "expiresUtc", expiresUtc.ToString("o") },
                    { "targetHwnd", Hwnd(target.Handle) }, { "coverHwnd", Hwnd(cover.Handle) },
                    { "targetIdentity", new Dictionary<string, object> { { "hwnd", target.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "pid", Process.GetCurrentProcess().Id }, { "start_time", startedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) } } },
                    { "coverIdentity", new Dictionary<string, object> { { "hwnd", cover.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "pid", Process.GetCurrentProcess().Id }, { "start_time", startedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) } } },
                    { "targetRect", RectValue(target.Handle) }, { "coverRect", RectValue(cover.Handle) },
                    { "targetBackgroundRgb", "#249CB7" }, { "counterBackgroundRgb", "#FFC44A" },
                    { "coverBackgroundRgb", "#263445" }, { "coverStripeRgb", "#E5883A" },
                    { "counter", counter }, { "value", textbox.Text }, { "lastEvent", lastEvent }, { "eventSequence", eventSequence },
                    { "stopRequestPath", stopPath }, { "readyPath", readyPath },
                    { "minimizeRequestPath", minimizePath }, { "windowState", target.WindowState.ToString() },
                    { "controls", new Dictionary<string, object> {
                        { "testValue", new Dictionary<string, object> { { "hwnd", Hwnd(textbox.Handle) }, { "controlId", GetDlgCtrlID(textbox.Handle) }, { "class", "Edit" }, { "name", "Test value" }, { "automationId", "TestValue" } } },
                        { "increment", new Dictionary<string, object> { { "hwnd", Hwnd(increment.Handle) }, { "controlId", GetDlgCtrlID(increment.Handle) }, { "class", "Button" }, { "name", "Increment" }, { "automationId", "Increment" } } },
                        { "counter", new Dictionary<string, object> { { "hwnd", Hwnd(counterLabel.Handle) }, { "controlId", GetDlgCtrlID(counterLabel.Handle) }, { "class", "Static" }, { "name", "Counter" }, { "automationId", "Counter" } } }
                    } }
                };
                string temporary = readyPath + ".new";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value));
                if (File.Exists(readyPath)) File.Replace(temporary, readyPath, null);
                else File.Move(temporary, readyPath);
            };
            increment.Click += delegate { counter++; counterLabel.Text = "Counter: " + counter; lastEvent = "increment"; eventSequence++; publish(); };
            textbox.TextChanged += delegate { if (target.Visible && cover.Visible) { lastEvent = "value-changed"; eventSequence++; publish(); } };

            target.Shown += delegate {
                SetControlId(textbox.Handle, 1001);
                SetControlId(increment.Handle, 1002);
                SetControlId(counterLabel.Handle, 1003);
                RECT rect;
                if (!GetWindowRect(target.Handle, out rect)) throw new InvalidOperationException("Cannot read fixture bounds.");
                cover.Bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                cover.Show(target); // Both forms override ShowWithoutActivation and WS_EX_NOACTIVATE.
                target.Update();
                cover.Update();
                publish();
            };
            var timer = new Timer { Interval = 100 };
            timer.Tick += delegate {
                if (File.Exists(stopPath) || DateTime.UtcNow >= expiresUtc) { timer.Stop(); cover.Close(); target.Close(); }
                else if (File.Exists(minimizePath) && target.WindowState != FormWindowState.Minimized) {
                    target.WindowState = FormWindowState.Minimized;
                    lastEvent = "self-minimized"; eventSequence++; publish();
                }
            };
            target.FormClosed += delegate {
                timer.Stop();
                if (!cover.IsDisposed) cover.Dispose();
                File.WriteAllText(Path.Combine(runDirectory, "stopped.json"), new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "instanceId", instanceId }, { "pid", Process.GetCurrentProcess().Id }, { "stoppedUtc", DateTime.UtcNow.ToString("o") },
                    { "reason", File.Exists(stopPath) ? "stop-request" : "expired-or-closed" }, { "counter", counter }, { "value", textbox.Text }
                }));
            };
            timer.Start();
            Application.Run(target);
            timer.Dispose();
            target.Dispose();
        }
    }
}
