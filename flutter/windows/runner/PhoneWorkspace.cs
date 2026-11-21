using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Runs on the user's desktop, at the shell user's integrity level.
internal sealed partial class PhoneWorkspace : Form
{
    private readonly string display;
    private readonly string stopEvent;
    private readonly Button appsButton = new Button();
    private readonly ListBox list = new ListBox();
    private readonly Label status = new Label();
    private readonly TextBox search = new TextBox();
    private readonly TabControl sections = new TabControl();
    private readonly Button restore = new Button();
    private List<Entry> running = new List<Entry>();
    private IntPtr currentWindow;
    private Size lastArea;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly List<Entry> installed = new List<Entry>();
    private readonly Dictionary<IntPtr, Placement> moved = new Dictionary<IntPtr, Placement>();
    private readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
    private Entry pending;
    private HashSet<IntPtr> beforeLaunch;
    private DateTime deadline;
    private bool expanded;
    private static bool Chinese { get { return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"; } }
    private static string TextFor(string en, string zh) { return Chinese ? zh : en; }

    private sealed class Entry
    {
        public string Name, AppId, Target, AppName, SearchText, ShellPath;
        public IntPtr Window;
        public uint Pid;
        public dynamic ShellItem;
        public override string ToString() { return String.IsNullOrEmpty(AppName) ? Name : AppName + "   " + Name; }
    }
    private sealed class Placement { public uint Pid; public int Token; public Native.WindowPlacement Original; public Native.Rect Bounds; }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2 || WindowsIdentity.GetCurrent().IsSystem ||
            !System.Text.RegularExpressions.Regex.IsMatch(args[0], @"^\\\\\.\\DISPLAY[0-9]+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            !args[1].StartsWith(@"Local\RustDeskPhone-", StringComparison.Ordinal)) return;
        Native.SetThreadDpiAwarenessContext(new IntPtr(-4)); // Per-monitor v2.
        Application.EnableVisualStyles();
        using (var form = new PhoneWorkspace(args[0], args[1]))
            if (form.SessionAlive() && form.TargetScreen() != null) Application.Run(form);
    }

    private PhoneWorkspace(string device, string stop)
    {
        display = device;
        stopEvent = stop;
        Text = "RustDesk";
        Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 14);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        appsButton.Text = TextFor("Apps", "应用");
        appsButton.Height = 48;
        appsButton.Dock = DockStyle.Bottom;
        appsButton.Click += delegate {
            if (expanded && currentWindow == IntPtr.Zero) return;
            ShowPicker(!expanded);
            if (!expanded) Native.SetForegroundWindow(currentWindow);
        };
        status.Height = 44;
        status.Dock = DockStyle.Top;
        list.Dock = DockStyle.Fill;
        list.IntegralHeight = false;
        list.ItemHeight = 36;
        list.MouseClick += delegate(object sender, MouseEventArgs e) {
            int index = list.IndexFromPoint(e.Location);
            if (index != ListBox.NoMatches) SelectApp((Entry)list.Items[index]);
        };
        list.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Enter && list.SelectedItem != null) SelectApp((Entry)list.SelectedItem);
        };
        var runningPage = new TabPage(TextFor("Running windows", "运行中的窗口"));
        var appsPage = new TabPage(TextFor("All apps", "全部应用"));
        sections.TabPages.Add(runningPage);
        sections.TabPages.Add(appsPage);
        sections.Dock = DockStyle.Fill;
        runningPage.Controls.Add(list);
        sections.SelectedIndexChanged += delegate {
            if (sections.SelectedTab == null) return;
            RefreshEntries();
        };
        search.Dock = DockStyle.Top;
        search.AccessibleName = TextFor("Search apps or window titles", "搜索应用或窗口标题");
        search.TextChanged += delegate { RefreshEntries(); };
        restore.Dock = DockStyle.Bottom;
        restore.Height = 44;
        restore.Text = TextFor("Return window to original screen", "将窗口还原到原屏幕");
        restore.Click += delegate {
            RestoreCurrent();
            ShowPicker(true);
        };
        Controls.Add(sections);
        Controls.Add(search);
        Controls.Add(status);
        Controls.Add(restore);
        Controls.Add(appsButton);
        InitializeLibrary();
        Shown += delegate { ShowPicker(true); };
        FormClosed += delegate { timer.Stop(); RestoreWindows(); ReturnOtherWindows(); };
        timer.Interval = 200;
        timer.Tick += delegate {
            var screen = TargetScreen();
            if (!SessionAlive() || screen == null) { Close(); return; }
            if (currentWindow != IntPtr.Zero && !ManagedWindowAlive()) {
                RestoreCurrent();
                ShowPicker(true);
            }
            if (lastArea != screen.WorkingArea.Size) {
                LayoutLibrary(screen.WorkingArea);
                if (currentWindow != IntPtr.Zero) FitWindow(currentWindow);
            }
            if (!expanded && ManagedWindowAlive()) FollowDialog(screen);
            if (pending == null) return;
            var match = FindLaunchedWindow();
            if (match != null) { pending = null; MoveWindowHere(match); }
            else if (DateTime.UtcNow >= deadline) {
                pending = null;
                ShowPicker(true);
                status.Text = TextFor("Select the opened window", "请选择刚打开的窗口");
            }
        };
        timer.Start();
    }

    private bool SessionAlive()
    {
        try {
            // Opening briefly also detects a crashed server: its event ceases to exist.
            using (var stop = EventWaitHandle.OpenExisting(stopEvent, EventWaitHandleRights.Synchronize))
                return !stop.WaitOne(0);
        } catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private Screen TargetScreen()
    {
        return Array.Find(Screen.AllScreens, s => String.Equals(s.DeviceName.TrimEnd('\0'), display, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowPicker(bool open)
    {
        var screen = TargetScreen();
        if (screen == null) { Close(); return; }
        expanded = open;
        sections.Visible = search.Visible = status.Visible = open;
        restore.Visible = open && currentWindow != IntPtr.Zero;
        LayoutLibrary(screen.WorkingArea);
        appsButton.Text = open && currentWindow != IntPtr.Zero
            ? TextFor("Back to window", "返回窗口") : TextFor("Apps", "应用");
        if (!open) return;
        pending = null;
        beforeLaunch = null;
        status.Text = TextFor("Search apps or window titles", "搜索应用或窗口标题");
        running = Windows();
        foreach (var entry in running) {
            entry.Target = Executable(entry.Pid);
            entry.AppName = System.IO.Path.GetFileNameWithoutExtension(entry.Target) ?? entry.Name;
        }
        PopulateGroups();
        RefreshEntries();
        Activate();
    }

    private void RefreshEntries()
    {
        bool all = sections.SelectedIndex == 1;
        if (all && installed.Count == 0) ReadApps();
        string query = search.Text.Trim();
        if (all) {
            FilterApps(query);
            return;
        }
        list.BeginUpdate();
        try {
            list.Items.Clear();
            foreach (var entry in running)
                if ((!groups.Visible || groups.SelectedIndex <= 0 || entry.AppName == (string)groups.SelectedItem) &&
                    MatchesSearch(entry, query))
                    list.Items.Add(entry);
        } finally { list.EndUpdate(); }
    }

    private void ReadApps()
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
        try {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder == null) return;
            foreach (dynamic item in folder.Items()) {
                string name = item.Name;
                if (String.IsNullOrEmpty(name)) continue;
                installed.Add(new Entry { Name = name, ShellItem = item, ShellPath = Convert.ToString(item.Path),
                    AppId = Convert.ToString(item.ExtendedProperty("System.AppUserModel.ID")),
                    Target = Convert.ToString(item.ExtendedProperty("System.Link.TargetParsingPath")) });
            }
            installed.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        } catch (COMException e) { status.Text = e.Message; }
        finally { Marshal.ReleaseComObject(shell); }
    }

    private List<Entry> Windows()
    {
        var entries = new List<Entry>();
        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused) {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == ownPid || hwnd == Native.GetShellWindow() || !Native.IsWindowVisible(hwnd) ||
                Native.GetWindow(hwnd, 4) != IntPtr.Zero || (Native.GetWindowLong(hwnd, -20) & 0x80) != 0) return true;
            int cloaked;
            if (Native.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(512);
            if (Native.GetWindowText(hwnd, title, title.Capacity) > 0)
                entries.Add(new Entry { Name = title.ToString(), Window = hwnd, Pid = pid });
            return true;
        }, IntPtr.Zero);
        return entries;
    }

    private void SelectApp(Entry entry)
    {
        if (entry.Window != IntPtr.Zero) { MoveWindowHere(entry); return; }
        try {
            entry.ShellItem.InvokeVerb();
            pending = entry;
            deadline = DateTime.UtcNow.AddSeconds(8);
            ShowPicker(false);
        } catch (COMException e) { status.Text = e.Message; }
    }

    private Entry FindLaunchedWindow()
    {
        var windows = Windows();
        IntPtr foreground = Native.GetForegroundWindow();
        windows.Sort((a,b) => (b.Window == foreground).CompareTo(a.Window == foreground));
        foreach (var candidate in windows) {
            if (beforeLaunch != null && beforeLaunch.Contains(candidate.Window)) continue;
            // Hosted and packaged apps can share a process; the window's AppUserModelID wins.
            if (!String.IsNullOrEmpty(pending.AppId)) {
                string windowId = Native.WindowAppId(candidate.Window);
                if (!String.IsNullOrEmpty(windowId)) {
                    if (String.Equals(windowId, pending.AppId, StringComparison.OrdinalIgnoreCase)) return candidate;
                    continue;
                }
            }
            IntPtr process = Native.OpenProcess(0x1000, false, candidate.Pid);
            if (process == IntPtr.Zero) continue;
            try {
                var id = new StringBuilder(512);
                int size = id.Capacity;
                if (!String.IsNullOrEmpty(pending.AppId) && Native.GetApplicationUserModelId(process, ref size, id) == 0 &&
                    String.Equals(id.ToString(), pending.AppId, StringComparison.OrdinalIgnoreCase)) return candidate;
                size = id.Capacity;
                if (!String.IsNullOrEmpty(pending.Target) && Native.QueryFullProcessImageName(process, 0, id, ref size) &&
                    String.Equals(id.ToString(), pending.Target, StringComparison.OrdinalIgnoreCase)) return candidate;
            } finally { Native.CloseHandle(process); }
        }
        return null;
    }

    private void MoveWindowHere(Entry entry)
    {
        uint pid;
        Native.GetWindowThreadProcessId(entry.Window, out pid);
        var screen = TargetScreen();
        if (pid != entry.Pid || screen == null) return;
        var placement = new Native.WindowPlacement();
        placement.Length = Marshal.SizeOf(placement);
        if (!Native.GetWindowPlacement(entry.Window, ref placement)) return;
        if (IsHungAppWindow(entry.Window)) {
            status.Text = TextFor("This application is not responding", "此应用暂时没有响应");
            return;
        }
        Native.Rect bounds;
        if (!moved.ContainsKey(entry.Window)) {
            if (!Native.GetWindowRect(entry.Window, out bounds)) return;
            if (placement.Show == 3) {
                // WINDOWPLACEMENT uses workspace coordinates, SetWindowPos uses screen coordinates.
                var origin = Screen.FromHandle(entry.Window);
                int dx = origin.WorkingArea.Left - origin.Bounds.Left;
                int dy = origin.WorkingArea.Top - origin.Bounds.Top;
                bounds = new Native.Rect { Left = placement.Left + dx, Top = placement.Top + dy,
                    Right = placement.Right + dx, Bottom = placement.Bottom + dy };
            }
            var saved = new Placement { Pid = pid, Original = placement, Bounds = bounds };
            if (!RememberWindow(entry.Window, saved)) {
                status.Text = TextFor("Cannot record the original window position", "无法记录窗口原位置");
                return;
            }
            moved.Add(entry.Window, saved);
        }
        var area = screen.WorkingArea;
        ShowPicker(false);
        Native.ShowWindowAsync(entry.Window, 9); // Restore before moving between monitors.
        if (!Native.SetWindowPos(entry.Window, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, 0x4014)) {
            ShowPicker(true);
            status.Text = TextFor("Windows could not move this window", "Windows 无法移动此窗口");
            return;
        }
        Native.ShowWindowAsync(entry.Window, 3); // Maximize using the destination work area.
        if (currentWindow != entry.Window) RestoreCurrent();
        currentWindow = entry.Window;
        FitWindow(currentWindow);
        Native.SetForegroundWindow(entry.Window);
    }

    private void RestoreCurrent()
    {
        Placement placement;
        if (moved.TryGetValue(currentWindow, out placement)) {
            RestoreWindow(currentWindow, placement);
            moved.Remove(currentWindow);
        }
        currentWindow = IntPtr.Zero;
    }

    private void RestoreWindows()
    {
        foreach (var item in moved) RestoreWindow(item.Key, item.Value);
    }

    private static void RestoreWindow(IntPtr window, Placement saved)
    {
        uint pid;
        Native.GetWindowThreadProcessId(window, out pid);
        if (pid != saved.Pid || !IsRecordedWindow(window, saved)) return;
        var placement = saved.Original;
        placement.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT for unresponsive or suspended apps.
        if (placement.Show == 3) Native.ShowWindowAsync(window, 9);
        else Native.SetWindowPlacement(window, ref placement);
        if (placement.Show == 1 || placement.Show == 3) {
            var r = saved.Bounds;
            Native.SetWindowPos(window, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left,
                r.Bottom - r.Top, 0x4014);
            if (placement.Show == 3) Native.ShowWindowAsync(window, 3);
        }
        ForgetWindow(window);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant { public ushort Type, Reserved1, Reserved2, Reserved3; public IntPtr Value, Extra; }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        }
        [DllImport("shell32")] private static extern int SHGetPropertyStoreForWindow(
            IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32")] private static extern int PropVariantClear(ref PropVariant value);
        internal static string WindowAppId(IntPtr window)
        {
            var iid = typeof(IPropertyStore).GUID;
            IPropertyStore store;
            if (SHGetPropertyStoreForWindow(window, ref iid, out store) != 0) return null;
            try {
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                PropVariant value;
                if (store.GetValue(ref key, out value) != 0) return null;
                try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) : null; }
                finally { PropVariantClear(ref value); }
            } finally { Marshal.ReleaseComObject(store); }
        }
        internal delegate bool EnumProc(IntPtr window, IntPtr data);
        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowPlacement { public int Length, Flags, Show; public Point Min, Max; public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect bounds);
        [DllImport("user32")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32")] internal static extern bool EnumWindows(EnumProc callback, IntPtr data);
        [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32")] internal static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
        [DllImport("user32")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32", CharSet=CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
        [DllImport("user32")] internal static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
        [DllImport("user32")] internal static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
        [DllImport("user32")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32")] internal static extern bool ShowWindowAsync(IntPtr hwnd, int command);
        [DllImport("user32")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int length);
        [DllImport("kernel32")] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32", CharSet=CharSet.Unicode)] internal static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder id);
        [DllImport("kernel32", CharSet=CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int length);
    }
}
