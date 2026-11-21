using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed partial class PhoneWorkspace
{
    private readonly ListBox groups = new ListBox();
    private readonly FlowLayoutPanel actions = new FlowLayoutPanel();
    private readonly Button tools = new Button();
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly ListView grid = new ListView();
    private readonly ImageList icons = new ImageList { ImageSize = new Size(40, 40), ColorDepth = ColorDepth.Depth32Bit };
    private readonly List<Entry> visibleApps = new List<Entry>();
    private Size originalSize;
    private bool captureEnabled;
    private DateTime quitRequested;
    private IntPtr quitting;
    private const string Preferences = @"HKEY_CURRENT_USER\Software\RustDesk\PhoneWorkspace";

    private void InitializeLibrary()
    {
        RecoverWindows();
        originalSize = TargetScreen() == null ? Size.Empty : TargetScreen().Bounds.Size;
        captureEnabled = Equals(Registry.GetValue(Preferences, "Capture", 0), 1);
        groups.Dock = DockStyle.Left;
        groups.Width = 190;
        groups.SelectedIndexChanged += delegate { RefreshEntries(); };
        sections.TabPages[0].Controls.Add(groups);
        grid.Dock = DockStyle.Fill;
        grid.View = View.LargeIcon;
        grid.MultiSelect = false;
        grid.VirtualMode = true;
        grid.LargeImageList = icons;
        grid.RetrieveVirtualItem += delegate(object sender, RetrieveVirtualItemEventArgs e) {
            var entry = visibleApps[e.ItemIndex];
            string key = entry.ShellPath ?? entry.Name;
            if (!icons.Images.ContainsKey(key)) {
                using (var icon = AppImage(entry)) icons.Images.Add(key, icon);
            }
            e.Item = new ListViewItem(entry.Name, key);
        };
        grid.MouseClick += delegate(object sender, MouseEventArgs e) {
            var item = grid.GetItemAt(e.X, e.Y);
            if (item != null) SelectApp(visibleApps[item.Index]);
        };
        grid.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Enter && grid.SelectedIndices.Count == 1)
                SelectApp(visibleApps[grid.SelectedIndices[0]]);
        };
        sections.TabPages[1].Controls.Add(grid);
        Disposed += delegate { icons.Dispose(); menu.Dispose(); };
        actions.AutoSize = true;
        actions.Dock = DockStyle.Bottom;
        tools.AutoSize = true;
        tools.Text = TextFor("Window actions", "窗口操作");
        tools.Click += delegate { BuildMenu(); menu.Show(tools, new Point(0, 0), ToolStripDropDownDirection.AboveRight); };
        actions.Controls.Add(tools);
        Controls.Add(actions);
        actions.BringToFront();
        sections.BringToFront();
    }

    private void FilterApps(string query)
    {
        grid.BeginUpdate();
        try {
            grid.VirtualListSize = 0;
            visibleApps.Clear();
            foreach (var e in installed) if (MatchesSearch(e, query)) visibleApps.Add(e);
            grid.VirtualListSize = visibleApps.Count;
        } finally { grid.EndUpdate(); }
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap);
    }
    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr obj);
    private static Bitmap AppImage(Entry entry)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        IShellItemImageFactory item;
        string path = String.IsNullOrEmpty(entry.AppId) ? entry.Target : @"shell:AppsFolder\" + entry.AppId;
        if (!String.IsNullOrEmpty(path) && SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item) == 0) {
            try {
                IntPtr bitmap;
                if (item.GetImage(new Size(40, 40), 4, out bitmap) == 0) {
                    try { return Image.FromHbitmap(bitmap); }
                    finally { DeleteObject(bitmap); }
                }
            } finally { Marshal.ReleaseComObject(item); }
        }
        return SystemIcons.Application.ToBitmap();
    }

    private const string PlacementMarker = "RustDesk.PhoneWorkspace.Placement";
    private static readonly string RecoveryKey = @"Software\RustDesk\PhoneWorkspace\Recovery\" +
        System.Diagnostics.Process.GetCurrentProcess().SessionId;
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32")] private static extern bool IsHungAppWindow(IntPtr window);

    private static bool RememberWindow(IntPtr window, Placement saved)
    {
        saved.Token = (Guid.NewGuid().GetHashCode() & 0x3fffffff) + 1;
        if (!SetProp(window, PlacementMarker, new IntPtr(saved.Token))) return false;
        var p = saved.Original;
        var r = saved.Bounds;
        using (var key = Registry.CurrentUser.CreateSubKey(RecoveryKey))
            key.SetValue(window.ToString(), String.Join(",", new long[] { saved.Token, saved.Pid,
                p.Flags, p.Show, p.Min.X, p.Min.Y, p.Max.X, p.Max.Y, p.Left, p.Top, p.Right, p.Bottom,
                r.Left, r.Top, r.Right, r.Bottom }));
        return true;
    }
    private static bool IsRecordedWindow(IntPtr window, Placement saved)
    {
        return GetProp(window, PlacementMarker) == new IntPtr(saved.Token);
    }
    private static void ForgetWindow(IntPtr window)
    {
        RemoveProp(window, PlacementMarker);
        using (var key = Registry.CurrentUser.OpenSubKey(RecoveryKey, true))
            if (key != null) key.DeleteValue(window.ToString(), false);
    }
    private static void RecoverWindows()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RecoveryKey, true)) {
            if (key == null) return;
            foreach (string name in key.GetValueNames()) {
                long handle;
                var fields = (key.GetValue(name) as string ?? "").Split(',');
                var v = new int[16];
                bool valid = fields.Length == v.Length;
                for (int i = 0; valid && i < v.Length; i++) valid = Int32.TryParse(fields[i], out v[i]);
                if (valid && Int64.TryParse(name, out handle)) {
                    var p = new Native.WindowPlacement { Length = Marshal.SizeOf(typeof(Native.WindowPlacement)),
                        Flags = v[2], Show = v[3], Min = new Point(v[4],v[5]), Max = new Point(v[6],v[7]),
                        Left = v[8], Top = v[9], Right = v[10], Bottom = v[11] };
                    RestoreWindow(new IntPtr(handle), new Placement { Token = v[0], Pid = (uint)v[1], Original = p,
                        Bounds = new Native.Rect { Left=v[12], Top=v[13], Right=v[14], Bottom=v[15] } });
                }
                key.DeleteValue(name, false);
            }
        }
    }

    private void ReturnOtherWindows()
    {
        var primary = Screen.PrimaryScreen;
        if (TargetScreen() == null || primary == null || primary.DeviceName == display || !IsPhoneAdapter()) return;
        var area = primary.WorkingArea;
        foreach (var e in Windows()) {
            if (moved.ContainsKey(e.Window) || Screen.FromHandle(e.Window).DeviceName != display ||
                IsHungAppWindow(e.Window)) continue;
            Native.Rect r;
            if (!Native.GetWindowRect(e.Window, out r)) continue;
            Native.SetWindowPos(e.Window, IntPtr.Zero, area.Left, area.Top,
                Math.Min(area.Width, r.Right-r.Left), Math.Min(area.Height, r.Bottom-r.Top), 0x4014);
        }
    }

    private void LayoutLibrary(Rectangle area)
    {
        lastArea = area.Size;
        int width = Math.Min(200, area.Width);
        Bounds = expanded ? area : new Rectangle(area.Left + (area.Width - width) / 2,
            area.Bottom - appsButton.Height, width, appsButton.Height);
        actions.Visible = expanded;
        groups.Visible = expanded && area.Width > area.Height;
    }

    private void PopulateGroups()
    {
        var previous = groups.SelectedItem as string;
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var entry in running) names.Add(entry.AppName);
        groups.BeginUpdate();
        try {
            groups.Items.Clear();
            groups.Items.Add(TextFor("All windows", "全部窗口"));
            foreach (string name in names) groups.Items.Add(name);
            groups.SelectedIndex = previous == null ? 0 : Math.Max(0, groups.Items.IndexOf(previous));
        } finally { groups.EndUpdate(); }
    }

    private bool MatchesSearch(Entry entry, string query)
    {
        if (query.Length == 0) return true;
        if (entry.SearchText == null) {
            var title = entry.Name + " " + entry.AppName;
            entry.SearchText = title + " " + Romanize(title);
        }
        return entry.SearchText.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    // Windows ships ICU on the same Windows 10 builds that support the phone display.
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern IntPtr utrans_openU(string id, int length, int direction, IntPtr rules,
        int ruleLength, IntPtr parseError, ref int error);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void utrans_transUChars(IntPtr trans, StringBuilder text, ref int length,
        int capacity, int start, ref int limit, ref int error);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void utrans_close(IntPtr trans);
    private IntPtr transliterator;
    private bool noTransliterator;

    private string Romanize(string value)
    {
        if (noTransliterator || !System.Text.RegularExpressions.Regex.IsMatch(value, @"[\u3400-\u9fff]")) return "";
        try {
            int error = 0;
            if (transliterator == IntPtr.Zero) {
                transliterator = utrans_openU("Han-Latin; Latin-ASCII", -1, 0, IntPtr.Zero, 0, IntPtr.Zero, ref error);
                if (transliterator == IntPtr.Zero) { noTransliterator = true; return ""; }
                Disposed += delegate {
                    if (transliterator == IntPtr.Zero) return;
                    utrans_close(transliterator);
                    transliterator = IntPtr.Zero;
                };
            }
            var buffer = new StringBuilder(value, value.Length * 12 + 1);
            int length = value.Length, limit = length;
            utrans_transUChars(transliterator, buffer, ref length, buffer.Capacity, 0, ref limit, ref error);
            if (error > 0) return "";
            string[] words = buffer.ToString().Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
            var initials = new StringBuilder();
            foreach (var word in words) initials.Append(word[0]);
            return String.Concat(words) + " " + initials;
        } catch (EntryPointNotFoundException) { noTransliterator = true; }
          catch (DllNotFoundException) { noTransliterator = true; }
        return "";
    }

    private static string Executable(uint pid)
    {
        IntPtr process = Native.OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return null;
        try {
            var path = new StringBuilder(1024);
            int n = path.Capacity;
            return Native.QueryFullProcessImageName(process, 0, path, ref n) ? path.ToString() : null;
        } finally { Native.CloseHandle(process); }
    }

    private bool ManagedWindowAlive()
    {
        Placement saved;
        uint pid;
        return currentWindow != IntPtr.Zero && moved.TryGetValue(currentWindow, out saved)
            && Native.GetWindowThreadProcessId(currentWindow, out pid) != 0 && pid == saved.Pid
            && IsRecordedWindow(currentWindow, saved);
    }

    private void BuildMenu()
    {
        menu.Items.Clear();
        if (ManagedWindowAlive()) {
            menu.Items.Add(TextFor("Larger text and controls", "放大文字和控件"), null, delegate { ChangeReadingSize(-1); });
            menu.Items.Add(TextFor("Smaller text and controls", "缩小文字和控件"), null, delegate { ChangeReadingSize(1); });
            menu.Items.Add(TextFor("New window", "新建窗口"), null, delegate { NewWindow(); });
            menu.Items.Add(TextFor("Close window", "关闭窗口"), null, delegate { CloseAppWindows(false); });
            menu.Items.Add(TextFor("Quit application", "退出应用"), null, delegate { CloseAppWindows(true); });
            if (quitting == currentWindow && DateTime.UtcNow - quitRequested >= TimeSpan.FromSeconds(10))
                menu.Items.Add(TextFor("Force quit application…", "强制退出应用…"), null, delegate { ForceQuit(); });
            if (captureEnabled)
                menu.Items.Add(TextFor("Copy or crop window screenshot", "窗口截图与裁剪"), null, delegate { CaptureWindow(); });
        }
        var capture = new ToolStripMenuItem(TextFor("Enable screenshots", "启用截图"));
        capture.Checked = captureEnabled;
        capture.Click += delegate {
            captureEnabled = !captureEnabled;
            Registry.SetValue(Preferences, "Capture", captureEnabled ? 1 : 0);
        };
        menu.Items.Add(capture);
        menu.Items.Add(TextFor("Leave phone screen", "退出手机屏"), null, delegate {
            if (Confirm(TextFor("Return windows and remove this phone screen?", "还原窗口并移除这块手机屏？"))) Close();
        });
    }

    private bool Confirm(string question)
    {
        return MessageBox.Show(this, question, "RustDesk", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void NewWindow()
    {
        if (!ManagedWindowAlive()) return;
        uint pid = moved[currentWindow].Pid;
        string target = Executable(pid), id = Native.WindowAppId(currentWindow);
        beforeLaunch = new HashSet<IntPtr>();
        foreach (var e in Windows()) beforeLaunch.Add(e.Window);
        string exe = Path.GetFileNameWithoutExtension(target) ?? "";
        string argument;
        switch (exe.ToLowerInvariant()) {
            case "chrome": case "msedge": case "brave": case "vivaldi":
                argument = "--new-window about:blank"; break;
            case "firefox": argument = "-new-window about:blank"; break;
            case "code": case "cursor": case "codium": argument = "--new-window"; break;
            default: argument = null; break;
        }
        if (argument != null) {
            try {
                using (System.Diagnostics.Process.Start(target, argument)) { }
                pending = new Entry { AppId = id, Target = target };
                deadline = DateTime.UtcNow.AddSeconds(8);
                ShowPicker(false);
                return;
            } catch (System.ComponentModel.Win32Exception e) { status.Text = e.Message; return; }
        }
        if (installed.Count == 0) ReadApps();
        var launcher = installed.Find(e => (!String.IsNullOrEmpty(id) && e.AppId == id) ||
            (!String.IsNullOrEmpty(target) && String.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase)));
        if (launcher == null) {
            status.Text = TextFor("Use the application's New Window command", "请使用应用内的新建窗口命令");
            return;
        }
        SelectApp(launcher, true);
    }

    [DllImport("user32")] private static extern bool PostMessage(IntPtr window, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32")] private static extern IntPtr GetLastActivePopup(IntPtr window);

    private void FollowDialog(Screen screen)
    {
        IntPtr dialog = GetLastActivePopup(currentWindow);
        if (dialog == IntPtr.Zero || dialog == currentWindow || !Native.IsWindowVisible(dialog) ||
            Native.GetWindow(dialog, 4) != currentWindow || Screen.FromHandle(dialog).DeviceName == display) return;
        Native.Rect rect;
        if (!Native.GetWindowRect(dialog, out rect)) return;
        var area = screen.WorkingArea;
        int x = area.Left + Math.Max(0, (area.Width - (rect.Right - rect.Left)) / 2);
        int y = area.Top + Math.Max(0, (area.Height - (rect.Bottom - rect.Top)) / 2);
        Native.SetWindowPos(dialog, IntPtr.Zero, x, y, 0, 0, 0x4015);
    }
    private void CloseAppWindows(bool all)
    {
        if (!ManagedWindowAlive() || !Confirm(all ? TextFor("Quit this application?", "退出此应用？")
                : TextFor("Close this window?", "关闭此窗口？"))) return;
        var window = currentWindow;
        uint pid = moved[window].Pid;
        ShowPicker(false);
        Native.SetForegroundWindow(window);
        if (all) {
            quitting = window;
            quitRequested = DateTime.UtcNow;
            string appId = Native.WindowAppId(window);
            string process = Path.GetFileNameWithoutExtension(Executable(pid)) ?? "";
            bool sharedHost = String.Equals(process, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                || String.Equals(process, "explorer", StringComparison.OrdinalIgnoreCase);
            foreach (var entry in Windows()) {
                if (entry.Pid == pid && entry.Window != window &&
                    (String.IsNullOrEmpty(appId) ? !sharedHost : Native.WindowAppId(entry.Window) == appId))
                    PostMessage(entry.Window, 0x10, IntPtr.Zero, IntPtr.Zero);
            }
        }
        if (!PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero)) {
            ShowPicker(true);
            status.Text = TextFor("The application could not be closed", "无法关闭应用");
        }
    }

    private void ForceQuit()
    {
        if (!ManagedWindowAlive() || currentWindow != quitting) return;
        uint pid = moved[currentWindow].Pid;
        string path = Executable(pid);
        if (String.IsNullOrEmpty(path) || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
            status.Text = TextFor("Windows manages this process", "此进程由 Windows 管理");
            return;
        }
        if (!Confirm(TextFor("Force quit and discard unsaved changes?", "强制退出并丢弃未保存的修改？"))) return;
        try {
            using (var process = System.Diagnostics.Process.GetProcessById((int)pid)) {
                var handle = process.Handle;
                if (ManagedWindowAlive() && moved[currentWindow].Pid == pid) process.Kill();
            }
        } catch (System.ComponentModel.Win32Exception e) { status.Text = e.Message; }
          catch (ArgumentException) { ShowPicker(true); }
          catch (InvalidOperationException) { ShowPicker(true); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 220, CharSet = CharSet.Unicode)]
    private struct Mode {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(76)] public int X;
        [FieldOffset(80)] public int Y;
        [FieldOffset(172)] public int Width;
        [FieldOffset(176)] public int Height;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Adapter {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, int index, ref Adapter adapter, uint flags);
    private bool IsPhoneAdapter()
    {
        for (int i = 0; ; i++) {
            var adapter = new Adapter { Size = Marshal.SizeOf(typeof(Adapter)) };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) return false;
            if (String.Equals(display, adapter.Name, StringComparison.OrdinalIgnoreCase))
                return (adapter.Flags & 1) != 0 && adapter.Description == "USB Mobile Monitor Virtual Display";
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMax { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int index, ref Mode mode);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref Mode mode, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, ref MinMax l,
        uint flags, uint timeout, out IntPtr result);

    private List<Size> ReadingSizes()
    {
        var sizes = new List<Size>();
        var screen = TargetScreen();
        if (screen == null || originalSize.IsEmpty) return sizes;
        int shortEdge = Math.Min(originalSize.Width, originalSize.Height);
        int longEdge = Math.Max(originalSize.Width, originalSize.Height);
        bool portrait = screen.Bounds.Width <= screen.Bounds.Height;
        for (int i = 0; ; i++) {
            var mode = new Mode { Size = 220 };
            if (!EnumDisplaySettings(display, i, ref mode)) break;
            int w = mode.Width, h = mode.Height;
            if (portrait != (w <= h)) continue;
            int s = Math.Min(w, h), l = Math.Max(w, h);
            if (s != shortEdge && s != 540 && s != 720) continue;
            if (l > longEdge || Math.Abs((double)l - (double)s * longEdge / shortEdge) > 1.1) continue;
            var size = new Size(w, h);
            if (!sizes.Contains(size)) sizes.Add(size);
        }
        sizes.Sort((a, b) => a.Width.CompareTo(b.Width));
        return sizes;
    }

    private void ChangeReadingSize(int direction)
    {
        if (!ManagedWindowAlive()) return;
        var screen = TargetScreen();
        if (screen == null) return;
        var choices = ReadingSizes();
        if (choices.Count == 0) return;
        int index = Math.Max(0, Math.Min(choices.Count - 1, choices.IndexOf(screen.Bounds.Size) + direction));
        string app = Executable(moved[currentWindow].Pid);
        if (app != null) Registry.SetValue(Preferences + @"\ReadingSize", app, index);
        FitWindow(currentWindow);
        ShowPicker(false);
        Native.SetForegroundWindow(currentWindow);
    }

    private void FitWindow(IntPtr window)
    {
        if (!ManagedWindowAlive() || window != currentWindow || !IsPhoneAdapter()) return;
        var screen = TargetScreen();
        var options = ReadingSizes();
        if (screen == null || options.Count == 0) return;
        string app = Executable(moved[window].Pid);
        object preferred = app == null ? null : Registry.GetValue(Preferences + @"\ReadingSize", app, 0);
        int index = preferred is int ? Math.Max(0, Math.Min((int)preferred, options.Count - 1)) : 0;
        var info = new MinMax();
        IntPtr result;
        if (SendMessageTimeout(window, 0x24, IntPtr.Zero, ref info, 2, 150, out result) == IntPtr.Zero) return;
        while (index < options.Count - 1 && (options[index].Width < info.MinTrack.X ||
            options[index].Height - (screen.Bounds.Height - screen.WorkingArea.Height) < info.MinTrack.Y)) index++;
        Size size = options[index];
        if (screen.Bounds.Size == size) return;
        var mode = new Mode { Size = 220 };
        if (!EnumDisplaySettings(display, -1, ref mode)) return;
        mode.Fields = 0x180000;
        mode.Width = size.Width;
        mode.Height = size.Height;
        var proposed = new Rectangle(mode.X, mode.Y, size.Width, size.Height);
        bool overlaps = false;
        int right = 0;
        foreach (var other in Screen.AllScreens) {
            if (other.DeviceName == display) continue;
            right = Math.Max(right, other.Bounds.Right);
            overlaps |= other.Bounds.IntersectsWith(proposed);
        }
        if (overlaps) { mode.Fields |= 0x20; mode.X = right; mode.Y = 0; }
        if (ChangeDisplaySettingsEx(display, ref mode, IntPtr.Zero, 0, IntPtr.Zero) != 0)
            status.Text = TextFor("Display size unavailable", "此显示尺寸不可用");
    }

    [DllImport("user32")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    private void CaptureWindow()
    {
        if (!captureEnabled || !ManagedWindowAlive()) return;
        Native.Rect rect;
        var screen = TargetScreen();
        if (screen == null || !Native.GetWindowRect(currentWindow, out rect) ||
            Screen.FromHandle(currentWindow).DeviceName != display) return;
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0 || (long)w * h > 16000000) return;
        using (var bitmap = new Bitmap(w, h)) {
            using (var graphics = Graphics.FromImage(bitmap)) {
                IntPtr dc = graphics.GetHdc();
                try {
                    if (!PrintWindow(currentWindow, dc, 2)) {
                        status.Text = TextFor("Window capture unavailable", "无法截取此窗口");
                        return;
                    }
                } finally { graphics.ReleaseHdc(dc); }
            }
            try {
                Clipboard.SetImage(bitmap);
                using (var editor = new CropForm(bitmap, screen.WorkingArea)) editor.ShowDialog(this);
            } catch (ExternalException) {
                status.Text = TextFor("Clipboard unavailable", "剪贴板暂时不可用");
            }
        }
    }

    private sealed class CropForm : Form
    {
        private Rectangle selection, original;
        private Point anchor;
        private int edges;
        private readonly PictureBox image = new PictureBox();
        private readonly Bitmap bitmap;
        public CropForm(Bitmap source, Rectangle area)
        {
            bitmap = source;
            selection = new Rectangle(Point.Empty, source.Size);
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = area;
            TopMost = true;
            image.Dock = DockStyle.Fill;
            image.SizeMode = PictureBoxSizeMode.Zoom;
            image.BackColor = Color.Black;
            image.Image = source;
            image.Paint += PaintSelection;
            image.MouseDown += StartCrop;
            image.MouseMove += UpdateCrop;
            image.MouseUp += delegate { image.Capture = false; };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            var done = new Button { Text = TextFor("Copy crop", "复制选区"), AutoSize = true, Height = 46 };
            var back = new Button { Text = TextFor("Keep original", "保留原图"), AutoSize = true, Height = 46 };
            done.Click += delegate {
                using (var crop = bitmap.Clone(selection, bitmap.PixelFormat)) {
                    try { Clipboard.SetImage(crop); Close(); }
                    catch (ExternalException) { done.Text = TextFor("Retry copy", "重试复制"); }
                }
            };
            back.Click += delegate { Close(); };
            bar.Controls.Add(done);
            bar.Controls.Add(back);
            Controls.Add(image);
            Controls.Add(bar);
        }
        private double ScaleFactor {
            get { return Math.Max(0.001, Math.Min((double)image.ClientSize.Width / bitmap.Width,
                (double)image.ClientSize.Height / bitmap.Height)); }
        }
        private Point Origin {
            get { return new Point((int)((image.Width - bitmap.Width * ScaleFactor) / 2),
                (int)((image.Height - bitmap.Height * ScaleFactor) / 2)); }
        }
        private Point SourcePoint(Point p) {
            return new Point(Math.Max(0, Math.Min(bitmap.Width, (int)((p.X - Origin.X) / ScaleFactor))),
                Math.Max(0, Math.Min(bitmap.Height, (int)((p.Y - Origin.Y) / ScaleFactor))));
        }
        private void PaintSelection(object sender, PaintEventArgs e)
        {
            double s = ScaleFactor;
            var r = new Rectangle(Origin.X + (int)(selection.X * s), Origin.Y + (int)(selection.Y * s),
                Math.Max(1, (int)(selection.Width * s) - 1), Math.Max(1, (int)(selection.Height * s) - 1));
            using (var pen = new Pen(Color.DeepSkyBlue, 3)) e.Graphics.DrawRectangle(pen, r);
        }
        private void StartCrop(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            anchor = SourcePoint(e.Location);
            original = selection;
            double slop = 22 / ScaleFactor;
            edges = (Math.Abs(anchor.X - selection.Left) < slop ? 1 :
                Math.Abs(anchor.X - selection.Right) < slop ? 2 : 0)
                | (Math.Abs(anchor.Y - selection.Top) < slop ? 4 :
                Math.Abs(anchor.Y - selection.Bottom) < slop ? 8 : 0);
            image.Capture = true;
        }
        private void UpdateCrop(object sender, MouseEventArgs e)
        {
            if (!image.Capture || e.Button != MouseButtons.Left) return;
            var p = SourcePoint(e.Location);
            if (edges == 0) {
                int x = Math.Min(bitmap.Width - 1, Math.Min(p.X, anchor.X));
                int y = Math.Min(bitmap.Height - 1, Math.Min(p.Y, anchor.Y));
                selection = Rectangle.FromLTRB(x, y, Math.Max(x + 1, Math.Max(p.X, anchor.X)),
                    Math.Max(y + 1, Math.Max(p.Y, anchor.Y)));
            } else {
                selection = Rectangle.FromLTRB(
                    (edges & 1) == 0 ? original.Left : Math.Min(original.Right - 1, p.X),
                    (edges & 4) == 0 ? original.Top : Math.Min(original.Bottom - 1, p.Y),
                    (edges & 2) == 0 ? original.Right : Math.Max(original.Left + 1, p.X),
                    (edges & 8) == 0 ? original.Bottom : Math.Max(original.Top + 1, p.Y));
            }
            image.Invalidate();
        }
    }
}
