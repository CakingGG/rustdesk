using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed partial class PhoneWorkspace
{
    private static readonly Color PageColor = Color.FromArgb(242, 242, 247);
    private static readonly Color Ink = Color.FromArgb(28, 28, 30);
    private static readonly Color SecondaryInk = Color.FromArgb(90, 90, 99);
    private readonly Label heading = new Label();
    private readonly Panel searchBox = new Panel();
    private readonly Button windowsTab = new Button(), appsTab = new Button(), more = new Button();
    private readonly PickerFlow cards = new PickerFlow();
    private readonly ReturnHandle returnHandle = new ReturnHandle();
    private readonly Dictionary<string, Bitmap> pictures = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
    private bool allApps, layingOut;
    private float unit;
    private Font body, caption, bold, title;

    private int Px(float value) { return (int)Math.Round(value * unit); }

    private void InitializePickerView()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = PageColor;
        heading.ForeColor = Ink;
        heading.TextAlign = ContentAlignment.MiddleLeft;
        status.ForeColor = SecondaryInk;
        status.TextAlign = ContentAlignment.MiddleLeft;
        searchBox.BackColor = Color.White;
        search.BorderStyle = BorderStyle.None;
        searchBox.Controls.Add(search);
        search.AccessibleName = TextFor("Search apps or window titles", "搜索应用或窗口标题");
        search.HandleCreated += delegate { SendCue(search.Handle, 0x1501, new IntPtr(1), search.AccessibleName); };
        search.TextChanged += delegate { RefreshEntries(); };
        ConfigureButton(windowsTab, TextFor("Windows", "窗口"), delegate { SwitchLibrary(false); });
        ConfigureButton(appsTab, TextFor("All apps", "全部应用"), delegate { SwitchLibrary(true); });
        ConfigureButton(more, TextFor("Actions", "操作"), delegate {
            BuildMenu();
            menu.Show(more, new Point(more.Width, more.Height), ToolStripDropDownDirection.BelowLeft);
        });
        ConfigureButton(appsButton, "", delegate { ReturnToWindow(); });
        returnHandle.Click += delegate { ShowPicker(true); };
        groups.BorderStyle = BorderStyle.None;
        groups.SelectedIndexChanged += delegate { if (!layingOut) RefreshEntries(); };
        status.TextChanged += delegate {
            if (expanded && lastArea.Width > 0 && !layingOut) LayoutContent();
        };
        Controls.AddRange(new Control[] { heading, searchBox, windowsTab, appsTab, more, appsButton, groups, cards, status });
        Disposed += delegate {
            returnHandle.Dispose(); menu.Dispose();
            foreach (var picture in pictures.Values) picture.Dispose();
            DisposeFonts();
        };
    }

    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendCue(IntPtr window, int message, IntPtr arg, string text);

    private static void ConfigureButton(Button button, string text, EventHandler click)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.ForeColor = Color.FromArgb(0, 93, 182);
        button.Text = text;
        button.Click += click;
    }

    private void SwitchLibrary(bool apps)
    {
        if (allApps == apps) return;
        allApps = apps;
        LayoutContent();
        RefreshEntries();
    }

    private void ReturnToWindow()
    {
        if (!ManagedWindowAlive()) return;
        ShowPicker(false);
        Native.SetForegroundWindow(currentWindow);
    }

    private void DisposeFonts()
    {
        if (body != null) { body.Dispose(); caption.Dispose(); bold.Dispose(); title.Dispose(); }
    }

    private void LayoutLibrary(Rectangle area)
    {
        lastArea = area.Size;
        // Work in phone-sized logical units even when the video is 864 or 1080 pixels wide.
        float scale = Math.Max(0.65f, Math.Min(area.Width, area.Height) / 390f);
        if (!expanded) {
            returnHandle.Place(area, scale);
            Hide();
            return;
        }
        returnHandle.Hide();
        layingOut = true;
        try {
            Bounds = area;
            if (unit != scale) {
                ClearCards();
                DisposeFonts();
                unit = scale;
                var family = SystemFonts.MessageBoxFont.FontFamily;
                body = new Font(family, Px(16), FontStyle.Regular, GraphicsUnit.Pixel);
                caption = new Font(family, Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
                bold = new Font(family, Px(16), FontStyle.Bold, GraphicsUnit.Pixel);
                title = new Font(family, Px(29), FontStyle.Bold, GraphicsUnit.Pixel);
                Font = body; heading.Font = title; status.Font = caption; menu.Font = body;
            }
            LayoutContent();
            RefreshEntries();
            if (!Visible) Show();
        } finally { layingOut = false; }
    }

    private void LayoutContent()
    {
        bool active = currentWindow != IntPtr.Zero;
        bool landscape = ClientSize.Width > ClientSize.Height;
        int pad = Px(14), w = ClientSize.Width - pad * 2, y = Px(4);
        appsButton.Visible = active;
        var current = running.Find(e => e.Window == currentWindow);
        appsButton.Text = "‹ " + (current == null ? TextFor("Back", "返回") : current.AppName);
        appsButton.TextAlign = ContentAlignment.MiddleLeft;
        appsButton.SetBounds(pad, y, w / 2, Px(44));
        more.SetBounds(ClientSize.Width - pad - Px(62), y, Px(62), Px(44));
        heading.Text = allApps ? TextFor("App library", "应用资源库") : TextFor("Windows", "窗口");
        if (active) y += Px(44);
        heading.SetBounds(pad, y, w - Px(70), Px(43)); y += Px(47);
        searchBox.SetBounds(pad, y, w, Px(42));
        search.SetBounds(Px(12), (searchBox.Height - search.PreferredHeight) / 2, w - Px(24), search.PreferredHeight);
        y += Px(49);
        windowsTab.SetBounds(pad, y, w / 2, Px(44));
        appsTab.SetBounds(pad + w / 2, y, w - w / 2, Px(44));
        windowsTab.BackColor = allApps ? Color.FromArgb(229, 229, 235) : Color.White;
        appsTab.BackColor = allApps ? Color.White : Color.FromArgb(229, 229, 235);
        windowsTab.Font = allApps ? body : bold; appsTab.Font = allApps ? bold : body;
        y += Px(51);
        status.Visible = status.Text.Length != 0;
        if (status.Visible) { status.SetBounds(pad, y, w, Px(38)); y += Px(38); }
        bool showGroups = landscape && !allApps;
        groups.Visible = showGroups;
        int sidebar = showGroups ? Math.Min(w / 3, Px(145)) : 0;
        int height = Math.Max(1, ClientSize.Height - y);
        groups.SetBounds(pad, y, sidebar, height);
        cards.SetBounds(pad + sidebar, y, Math.Max(1,w - sidebar), height);
    }

    private void ClearCards()
    {
        for (int i = cards.Controls.Count - 1; i >= 0; --i) cards.Controls[i].Dispose();
    }

    private void RefreshEntries()
    {
        if (!expanded || unit <= 0) return;
        string query = search.Text.Trim();
        cards.SuspendLayout();
        try {
            ClearCards();
            int width = Math.Max(Px(70), cards.Width - SystemInformation.VerticalScrollBarWidth);
            if (allApps) {
                if (installed.Count == 0) ReadApps();
                int columns = Math.Max(2, width / Px(82));
                foreach (var e in installed)
                    if (MatchesSearch(e, query)) AddTile(e, width / columns, true);
            } else {
                var current = running.Find(e => e.Window == currentWindow);
                if (current != null && MatchesSearch(current, query)) {
                    AddSection(TextFor("Current window", "当前窗口"), width);
                    AddTile(current, width, false);
                    var row = new FlowLayoutPanel { Size = new Size(width, Px(49)), BackColor = Color.White, Margin = new Padding(0) };
                    AddAction(row, "A−", TextFor("Smaller text", "缩小文字"), width / 4, delegate { ChangeReadingSize(1); });
                    AddAction(row, "A+", TextFor("Larger text", "放大文字"), width / 4, delegate { ChangeReadingSize(-1); });
                    AddAction(row, TextFor("Restore", "还原窗口"), TextFor("Restore window", "还原窗口"), width / 2, delegate { RestoreCurrent(); ShowPicker(true); });
                    cards.Controls.Add(row);
                }
                AddSection(current == null ? TextFor("Running windows", "运行中的窗口") : TextFor("Other windows", "其他窗口"), width);
                foreach (var e in running)
                    if (e != current && (lastArea.Width <= lastArea.Height || groups.SelectedIndex <= 0 || e.AppName == (string)groups.SelectedItem) &&
                        MatchesSearch(e, query)) AddTile(e, width, false);
            }
            if (cards.Controls.Count == 0 || (cards.Controls.Count == 1 && cards.Controls[0] is Label))
                AddSection(TextFor("No matches", "没有匹配结果"), width);
        } finally {
            cards.ResumeLayout(true);
            cards.AutoScrollPosition = Point.Empty;
        }
    }

    private void AddAction(FlowLayoutPanel row, string text, string name, int width, EventHandler click)
    {
        var button = new Button { Size = new Size(width, Px(46)), Margin = Padding.Empty, AccessibleName = name };
        ConfigureButton(button, text, click);
        row.Controls.Add(button);
    }

    private void AddSection(string text, int width)
    {
        cards.Controls.Add(new Label { Text = text, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SecondaryInk,
            Font = caption, Size = new Size(width, Px(32)), Margin = Padding.Empty });
    }

    private void AddTile(Entry entry, int width, bool grid)
    {
        var tile = new AppTile(this, entry, grid) {
            Size = new Size(width - Px(grid ? 5 : 0), Px(grid ? 92 : 66)),
            Margin = new Padding(0, 0, Px(grid ? 5 : 0), Px(6))
        };
        tile.Click += delegate { SelectApp(entry); };
        cards.Controls.Add(tile);
    }

    private sealed class PickerFlow : FlowLayoutPanel
    {
        public PickerFlow() { DoubleBuffered = true; AutoScroll = true; }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // Match the high-resolution Windows wheel units sent by the phone.
            AutoScrollPosition = new Point(0, Math.Max(0, -AutoScrollPosition.Y - (int)Math.Round(e.Delta * 100.0 / 120)));
            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }
    }

    private sealed class AppTile : Button
    {
        private readonly PhoneWorkspace picker;
        private readonly Entry entry;
        private readonly bool grid;
        public AppTile(PhoneWorkspace owner, Entry app, bool isGrid)
        {
            picker = owner; entry = app; grid = isGrid;
            AccessibleName = grid ? entry.Name : entry.ToString();
            FlatStyle = FlatStyle.Flat;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(PageColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = Rounded(ClientRectangle, picker.Px(12))) {
                g.FillPath(Brushes.White, shape);
                if (Focused && ShowFocusCues) g.DrawPath(Pens.RoyalBlue, shape);
            }
            string key = entry.AppId ?? entry.Target ?? entry.ShellPath ?? entry.Name;
            Bitmap image;
            if (!picker.pictures.TryGetValue(key, out image)) picker.pictures[key] = image = AppImage(entry);
            int edge = picker.Px(grid ? 40 : 38), pad = picker.Px(12);
            var rect = new Rectangle(grid ? (Width - edge) / 2 : pad, grid ? picker.Px(7) : (Height-edge)/2, edge, edge);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, rect);
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            if (grid) {
                int top = rect.Bottom + picker.Px(3);
                using (var format = new StringFormat { Alignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.LineLimit, Trimming = StringTrimming.EllipsisCharacter })
                using (var brush = new SolidBrush(Ink)) {
                    g.DrawString(entry.Name, picker.caption, brush, new RectangleF(picker.Px(3), top,
                        Width - picker.Px(6), Math.Min(Height-top, (float)Math.Ceiling(2*picker.caption.GetHeight(g))+1)), format);
                }
            } else {
                int left = rect.Right + pad, w = Math.Max(1, Width-left-pad);
                flags |= TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
                TextRenderer.DrawText(g, entry.AppName ?? entry.Name, picker.bold, new Rectangle(left,picker.Px(8),w,picker.Px(25)), Ink, flags);
                TextRenderer.DrawText(g, entry.Name, picker.caption, new Rectangle(left,picker.Px(33),w,picker.Px(24)), SecondaryInk, flags);
            }
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(2*radius, Math.Min(r.Width,r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.Left,r.Top,d,d,180,90); path.AddArc(r.Right-d,r.Top,d,d,270,90);
        path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); path.AddArc(r.Left,r.Bottom-d,d,d,90,90);
        path.CloseFigure(); return path;
    }

    private sealed class ReturnHandle : Form
    {
        public ReturnHandle()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 67, 124);
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            AccessibleName = TextFor("Switch application", "切换应用");
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var font = new Font(SystemFonts.MessageBoxFont.FontFamily, ClientSize.Height * 0.34f,
                       FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(e.Graphics, AccessibleName, font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        public void Place(Rectangle area, float scale)
        {
            var size = new Size((int)(144 * scale), (int)(44 * scale));
            var inset = (int)(8 * scale);
            Bounds = new Rectangle(area.Right - size.Width - inset, area.Bottom - size.Height - inset,
                size.Width, size.Height);
            if (!Visible) Show();
        }
    }
}
