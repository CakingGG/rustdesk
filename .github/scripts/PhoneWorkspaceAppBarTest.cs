using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class PhoneWorkspaceAppBarTest
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Bounds, Work; public int Flags; }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string type, string title);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string type, string title);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr button, IntPtr point);
    [DllImport("user32")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private static Rect WorkArea(IntPtr window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) throw new Exception("Missing monitor");
        return info.Work;
    }
    private static void WaitFor(Func<bool> condition, string error)
    {
        var time = Stopwatch.StartNew();
        while (!condition()) {
            if (time.Elapsed.TotalSeconds > 12) throw new Exception(error);
            Application.DoEvents();
            Thread.Sleep(30);
        }
    }
    private static IEnumerable<Control> Children(Control parent)
    {
        foreach (Control child in parent.Controls) {
            yield return child;
            foreach (var item in Children(child)) yield return item;
        }
    }
    private static void CheckNavigation(IntPtr app, string title)
    {
        var picker = Application.OpenForms.Cast<Form>().Single(f => f.Visible);
        var original = WorkArea(picker.Handle);
        var tile = Children(picker).OfType<Button>().Single(b => (b.AccessibleName ?? "").Contains(title));
        tile.PerformClick();
        WaitFor(() => IsZoomed(app) && WorkArea(app).Bottom < original.Bottom,
            "Windows did not reserve navigation space for the maximized application");
        var bar = Application.OpenForms.Cast<Form>().Single(f => f.Visible && f != picker);
        Rect client, sendRect;
        if (!GetClientRect(app, out client)) throw new Exception("Missing application client");
        var bottom = new Point(client.Right, client.Bottom);
        if (!ClientToScreen(app, ref bottom) || bottom.Y > bar.Top || WorkArea(app).Bottom != bar.Top)
            throw new Exception("The switch bar overlaps the application's client area");
        var send = FindWindowEx(app, IntPtr.Zero, null, "Send");
        if (!GetWindowRect(send, out sendRect) ||
            WindowFromPoint(new Point((sendRect.Left + sendRect.Right) / 2, (sendRect.Top + sendRect.Bottom) / 2)) != send)
            throw new Exception("The bottom-right Send button cannot receive a click");
        var point = new Point(bar.Height, bar.Height / 2);
        if (WindowFromPoint(bar.PointToScreen(point)) != bar.Handle) throw new Exception("Missing left navigation button");
        var coordinates = new IntPtr(point.X | (point.Y << 16));
        SendMessage(bar.Handle, 0x201, new IntPtr(1), coordinates);
        SendMessage(bar.Handle, 0x202, IntPtr.Zero, coordinates);
        WaitFor(() => picker.Visible && !bar.Visible && WorkArea(app).Bottom == original.Bottom,
            "Switching did not reopen the picker and restore the original work area");
        Console.WriteLine("Bottom-right Send button received the hit. The picker reopened. Work area bottom {0} -> {1} -> {0}.",
            original.Bottom, bottom.Y);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (args[0] == "--window") {
            using (var form = new Form { Text = args[1] }) {
                var send = new Button { Text = "Send", Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
                send.Location = new Point(form.ClientSize.Width - 100, form.ClientSize.Height - 50);
                form.Controls.Add(send);
                Application.Run(form);
            }
            return 0;
        }
        string id = Guid.NewGuid().ToString("N"), title = "LayoutTest" + id;
        string eventName = @"Local\RustDeskPhone-" + id;
        using (var fixture = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--window " + title)
            { UseShellExecute = false, CreateNoWindow = true }))
        using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
        using (var timer = new System.Windows.Forms.Timer { Interval = 600 }) {
            try {
                WaitFor(() => FindWindow(null, title) != IntPtr.Zero, "Test application did not start");
                Exception failure = null;
                timer.Tick += delegate {
                    timer.Stop();
                    try { CheckNavigation(FindWindow(null, title), title); }
                    catch (Exception ex) { failure = ex; }
                    finally { stop.Set(); }
                };
                timer.Start();
                var screen = Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen;
                Assembly.LoadFrom(args[0]).EntryPoint.Invoke(null, new object[] {
                    new string[] { screen.DeviceName.TrimEnd('\0'), eventName }
                });
                if (timer.Enabled) throw new Exception("The picker exited before the test application was selected");
                if (failure != null) throw failure;
            } catch (Exception ex) {
                Console.Error.WriteLine(ex);
                return 1;
            } finally {
                if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(3000)) fixture.Kill(); }
            }
        }
        return 0;
    }
}
