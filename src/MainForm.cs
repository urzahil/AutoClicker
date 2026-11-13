using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace Snap
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Native.timeBeginPeriod(1); // 1 ms timer resolution for accurate intervals
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class MainForm : Form
    {
        const int VkF6 = 0x75;
        static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        static readonly string ConfigPath = Path.Combine(AppData, "Snap", "config.json");
        // Settings from before the app was renamed; read once if the new file doesn't exist yet.
        static readonly string LegacyConfigPath = Path.Combine(AppData, "AutoClicker", "config.json");

        readonly List<ClickPoint> points = new List<ClickPoint>();
        readonly ListView list = new ListView();
        readonly PointEditor editor = new PointEditor();
        readonly Button startBtn = new Button();
        readonly Button hotkeyBtn = new Button();
        readonly Label status = new Label();
        readonly ToolTip statusTip = new ToolTip();
        readonly CheckBox keepAwakeBox = new CheckBox();
        readonly CheckBox returnCursorBox = new CheckBox();
        readonly List<Button> listButtons = new List<Button>();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Timer poll = new Timer();
        Clicker runner;
        int hotkey = VkF6;
        bool capturing;
        bool keyWasDown;
        bool loaded;
        Rectangle savedBounds = Rectangle.Empty; // window position/size from config.json
        bool savedMaximized;

        public MainForm()
        {
            Text = "Snap " + AppInfo.Version;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(780, 480);
            MinimumSize = new Size(760, 440);

            BuildLayout();
            BuildMenu();
            LoadConfig();
            if (IsOnScreen(savedBounds)) StartPosition = FormStartPosition.Manual;
            else savedBounds = Rectangle.Empty;
            UpdateLabels();

            poll.Interval = 50;
            poll.Tick += delegate { PollHotkey(); };
            poll.Start();
        }

        // ---------------------------------------------------------------- layout
        void BuildLayout()
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // top bar
            var top = new TableLayoutPanel();
            top.AutoSize = true;
            top.Dock = DockStyle.Fill;
            top.ColumnCount = 5;
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            startBtn.AutoSize = true;
            startBtn.MinimumSize = new Size(110, 0);
            startBtn.Click += delegate { Toggle(); };
            status.Text = "Stopped";
            status.AutoSize = true;
            status.Anchor = AnchorStyles.Left;
            status.Margin = new Padding(12, 0, 12, 0);
            hotkeyBtn.AutoSize = true;
            hotkeyBtn.Text = "Change hotkey…";
            hotkeyBtn.Click += delegate { BeginCapture(); };
            keepAwakeBox.Text = "Keep PC awake while running";
            keepAwakeBox.AutoSize = true;
            keepAwakeBox.Anchor = AnchorStyles.Left;
            keepAwakeBox.Margin = new Padding(12, 0, 0, 0);
            new ToolTip().SetToolTip(keepAwakeBox,
                "Stops sleep, the screen saver and the inactivity lock while clicking.\n" +
                "After about 7½ minutes without input (±20 s), the mouse is nudged by 1 pixel and back.");
            keepAwakeBox.CheckedChanged += delegate { SaveConfig(); };
            returnCursorBox.Text = "Return cursor after each click";
            returnCursorBox.AutoSize = true;
            returnCursorBox.Anchor = AnchorStyles.Left;
            returnCursorBox.Margin = new Padding(12, 0, 0, 0);
            new ToolTip().SetToolTip(returnCursorBox,
                "After each click, move the mouse back to where it was before the click.");
            returnCursorBox.CheckedChanged += delegate { SaveConfig(); };
            // stacked, so the second checkbox doesn't widen the top row past the window
            var options = new FlowLayoutPanel { AutoSize = true, WrapContents = false,
                FlowDirection = FlowDirection.TopDown, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            options.Controls.Add(keepAwakeBox);
            options.Controls.Add(returnCursorBox);
            var load = new Button { Text = "Load…", AutoSize = true };
            listButtons.Add(load); // loading replaces the list, so lock it while running
            var save = new Button { Text = "Save…", AutoSize = true };
            load.Click += delegate { LoadFile(); };
            save.Click += delegate { SaveFile(); };
            var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            right.Controls.Add(load);
            right.Controls.Add(save);
            top.Controls.Add(startBtn, 0, 0);
            top.Controls.Add(status, 1, 0);
            top.Controls.Add(hotkeyBtn, 2, 0);
            top.Controls.Add(options, 3, 0);
            top.Controls.Add(right, 4, 0);
            top.Margin = new Padding(8);
            root.Controls.Add(top, 0, 0);

            // point list
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.GridLines = true;
            string[] cols = { "#", "On", "X", "Y", "Button", "Type", "Interval", "± Offset (ms)" };
            int[] widths = { 40, 45, 70, 70, 80, 80, 120, 120 };
            for (int i = 0; i < cols.Length; i++)
                list.Columns.Add(cols[i], widths[i], HorizontalAlignment.Center);
            list.DoubleClick += delegate { ToggleEnabled(); };
            list.MouseUp += OnListMouseUp;
            list.Margin = new Padding(8, 0, 8, 0);
            root.Controls.Add(list, 0, 1);

            // new point panel
            var box = new GroupBox { Text = "New point", AutoSize = true, Dock = DockStyle.Fill };
            box.Margin = new Padding(8);
            box.Padding = new Padding(8, 4, 8, 8);
            editor.Dock = DockStyle.Fill;
            box.Controls.Add(editor);
            root.Controls.Add(box, 0, 2);

            // buttons
            var bar = new TableLayoutPanel();
            bar.AutoSize = true;
            bar.Dock = DockStyle.Fill;
            bar.ColumnCount = 2;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            AddListButton(btns, "Add point", AddPoint);
            AddListButton(btns, "Remove", Remove);
            AddListButton(btns, "Move up", delegate { MovePoint(-1); });
            AddListButton(btns, "Move down", delegate { MovePoint(1); });
            var hint = new Label
            {
                Text = "Right-click a point to edit it · double-click to enable/disable",
                AutoSize = true,
                Anchor = AnchorStyles.Right
            };
            bar.Controls.Add(btns, 0, 0);
            bar.Controls.Add(hint, 1, 0);
            bar.Margin = new Padding(8, 0, 8, 8);
            root.Controls.Add(bar, 0, 3);
        }

        void AddListButton(FlowLayoutPanel parent, string text, Action action)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += delegate { action(); };
            parent.Controls.Add(b);
            listButtons.Add(b);
        }

        void BuildMenu()
        {
            menu.Items.Add("Edit…", null, delegate { Edit(); });
            menu.Items.Add("Enable / disable", null, delegate { ToggleEnabled(); });
            menu.Items.Add("Duplicate", null, delegate { Duplicate(); });
            menu.Items.Add("Remove", null, delegate { Remove(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Move up", null, delegate { MovePoint(-1); });
            menu.Items.Add("Move down", null, delegate { MovePoint(1); });
        }

        // --------------------------------------------------------- list handling
        int Selected
        {
            get { return list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1; }
        }

        void RefreshList(int select)
        {
            list.BeginUpdate();
            list.Items.Clear();
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                var item = new ListViewItem((i + 1).ToString());
                item.SubItems.Add(p.Enabled ? "✔" : "—");
                item.SubItems.Add(p.X.ToString());
                item.SubItems.Add(p.Y.ToString());
                item.SubItems.Add(p.Button);
                item.SubItems.Add(p.ClickType);
                item.SubItems.Add(ClickPoint.FormatInterval(p.IntervalMs));
                item.SubItems.Add(p.OffsetMs.ToString());
                list.Items.Add(item);
            }
            list.EndUpdate();
            if (select >= 0 && select < points.Count)
            {
                list.Items[select].Selected = true;
                list.Items[select].EnsureVisible();
            }
        }

        void OnListMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || runner != null) return;
            var hit = list.GetItemAt(e.X, e.Y);
            if (hit == null) return;
            hit.Selected = true;
            menu.Show(list, e.Location);
        }

        void AddPoint()
        {
            var p = editor.Read();
            if (p == null) return;
            points.Add(p);
            RefreshList(points.Count - 1);
        }

        void Edit()
        {
            int i = Selected;
            if (i < 0) return;
            using (var dlg = new EditDialog(points[i]))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dlg.Result.Enabled = points[i].Enabled;
                points[i] = dlg.Result;
                RefreshList(i);
            }
        }

        void Duplicate()
        {
            int i = Selected;
            if (i < 0) return;
            points.Insert(i + 1, points[i].Clone());
            RefreshList(i + 1);
        }

        void Remove()
        {
            int i = Selected;
            if (i < 0) return;
            points.RemoveAt(i);
            RefreshList(Math.Min(i, points.Count - 1));
        }

        void MovePoint(int d)
        {
            int i = Selected;
            if (i < 0 || i + d < 0 || i + d >= points.Count) return;
            var t = points[i]; points[i] = points[i + d]; points[i + d] = t;
            RefreshList(i + d);
        }

        void ToggleEnabled()
        {
            int i = Selected;
            if (i < 0) return;
            points[i].Enabled = !points[i].Enabled;
            RefreshList(i);
            if (runner != null) runner.NotifyChanged(); // takes effect immediately while running
        }

        // ----------------------------------------------------------- run control
        void Toggle()
        {
            if (runner != null) { runner.Stop(); return; }
            if (!points.Exists(delegate(ClickPoint p) { return p.Enabled; }))
            {
                MessageBox.Show(this, "Add or enable at least one point.", "Nothing to do",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            runner = new Clicker(points, keepAwakeBox.Checked, returnCursorBox.Checked,
                delegate { BeginInvoke(new Action(Stopped)); },
                delegate(string problem) { BeginInvoke(new Action(delegate { ShowProblem(problem); })); });
            statusTip.SetToolTip(status, null);
            runner.Start();
            status.Text = "Running…";
            SetLocked(true);
            UpdateLabels();
        }

        /// <summary>Flags a problem the clicker hit; the details go in the status tooltip.</summary>
        void ShowProblem(string problem)
        {
            if (runner != null) status.Text = "Running – move blocked";
            statusTip.SetToolTip(status, problem);
        }

        void Stopped()
        {
            runner = null;
            status.Text = string.IsNullOrEmpty(statusTip.GetToolTip(status))
                ? "Stopped" : "Stopped – move was blocked";
            SetLocked(false);
            UpdateLabels();
        }

        void SetLocked(bool locked)
        {
            editor.SetLocked(locked);
            keepAwakeBox.Enabled = !locked;
            returnCursorBox.Enabled = !locked;
            foreach (var b in listButtons) b.Enabled = !locked;
        }

        // ---------------------------------------------------------------- hotkey
        void UpdateLabels()
        {
            startBtn.Text = (runner != null ? "Stop" : "Start") + " (" + VkName(hotkey) + ")";
        }

        void BeginCapture()
        {
            capturing = true;
            hotkeyBtn.Text = "Press a key… (Esc cancels)";
            hotkeyBtn.Enabled = false;
        }

        void EndCapture(bool save)
        {
            capturing = false;
            hotkeyBtn.Text = "Change hotkey…";
            hotkeyBtn.Enabled = true;
            UpdateLabels();
            if (save) SaveConfig();
        }

        static bool Skippable(int vk)
        {
            // mouse buttons, Esc, modifiers
            return vk <= 6 || vk == 0x1B || (vk >= 0x10 && vk <= 0x12) ||
                   vk == 0x5B || vk == 0x5C || (vk >= 0xA0 && vk <= 0xA5);
        }

        void PollHotkey()
        {
            if (capturing)
            {
                if (Native.IsDown(0x1B)) EndCapture(false);
                else
                {
                    for (int vk = 8; vk < 255; vk++)
                    {
                        if (Skippable(vk) || !Native.IsDown(vk)) continue;
                        hotkey = vk;
                        EndCapture(true);
                        break;
                    }
                }
                keyWasDown = true; // swallow the key that was just pressed
                return;
            }
            bool down = Native.IsDown(hotkey);
            if (down && !keyWasDown) Toggle();
            keyWasDown = down;
        }

        static string VkName(int vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
            if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
            switch (vk)
            {
                case 0x08: return "Backspace"; case 0x09: return "Tab"; case 0x0D: return "Enter";
                case 0x13: return "Pause"; case 0x14: return "CapsLock"; case 0x20: return "Space";
                case 0x21: return "PageUp"; case 0x22: return "PageDown"; case 0x23: return "End";
                case 0x24: return "Home"; case 0x25: return "Left"; case 0x26: return "Up";
                case 0x27: return "Right"; case 0x28: return "Down"; case 0x2D: return "Insert";
                case 0x2E: return "Delete"; case 0x91: return "ScrollLock";
            }
            return "Key 0x" + vk.ToString("X2");
        }

        // ----------------------------------------------------------- persistence
        void LoadConfig()
        {
            try
            {
                string path = File.Exists(ConfigPath) || !File.Exists(LegacyConfigPath)
                    ? ConfigPath : LegacyConfigPath;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path));
                if (d.ContainsKey("hotkey")) hotkey = Convert.ToInt32(d["hotkey"]);
                if (d.ContainsKey("keep_awake")) keepAwakeBox.Checked = Convert.ToBoolean(d["keep_awake"]);
                if (d.ContainsKey("return_cursor")) returnCursorBox.Checked = Convert.ToBoolean(d["return_cursor"]);
                if (d.ContainsKey("window_x") && d.ContainsKey("window_y") &&
                    d.ContainsKey("window_width") && d.ContainsKey("window_height"))
                    savedBounds = new Rectangle(
                        Convert.ToInt32(d["window_x"]), Convert.ToInt32(d["window_y"]),
                        Convert.ToInt32(d["window_width"]), Convert.ToInt32(d["window_height"]));
                if (d.ContainsKey("window_maximized")) savedMaximized = Convert.ToBoolean(d["window_maximized"]);
            }
            catch { }
            loaded = true;
        }

        void SaveConfig()
        {
            if (!loaded) return; // don't overwrite the file while LoadConfig is still applying it
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                var d = new Dictionary<string, object>();
                d["hotkey"] = hotkey;
                d["keep_awake"] = keepAwakeBox.Checked;
                d["return_cursor"] = returnCursorBox.Checked;
                // RestoreBounds is the normal-size rectangle when the window is maximized or minimized
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                d["window_x"] = b.X; d["window_y"] = b.Y;
                d["window_width"] = b.Width; d["window_height"] = b.Height;
                d["window_maximized"] = WindowState == FormWindowState.Maximized;
                File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }

        // Same JSON layout as the original Python version, so saved setups still load.
        void SaveFile()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "JSON (*.json)|*.json";
                dlg.DefaultExt = "json";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var rows = new List<Dictionary<string, object>>();
                foreach (var p in points)
                {
                    var d = new Dictionary<string, object>();
                    d["x"] = p.X; d["y"] = p.Y;
                    d["interval_ms"] = p.IntervalMs; d["offset_ms"] = p.OffsetMs;
                    d["button"] = p.Button; d["click_type"] = p.ClickType; d["enabled"] = p.Enabled;
                    rows.Add(d);
                }
                File.WriteAllText(dlg.FileName, new JavaScriptSerializer().Serialize(rows));
            }
        }

        void LoadFile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "JSON (*.json)|*.json";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(
                        File.ReadAllText(dlg.FileName));
                    var loaded = new List<ClickPoint>();
                    foreach (var d in rows)
                    {
                        var p = new ClickPoint();
                        p.X = Convert.ToInt32(d["x"]); p.Y = Convert.ToInt32(d["y"]);
                        p.IntervalMs = Convert.ToInt32(d["interval_ms"]);
                        p.OffsetMs = Convert.ToInt32(d["offset_ms"]);
                        p.Button = (string)d["button"]; p.ClickType = (string)d["click_type"];
                        p.Enabled = Convert.ToBoolean(d["enabled"]);
                        loaded.Add(p);
                    }
                    points.Clear();
                    points.AddRange(loaded);
                    RefreshList(points.Count > 0 ? 0 : -1);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Load failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// Restores the saved window position and size. Done here rather than in the constructor
        /// so it happens after DPI auto-scaling, which would otherwise rescale the saved size.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (savedBounds.IsEmpty) return;
            Bounds = savedBounds;
            if (savedMaximized) WindowState = FormWindowState.Maximized;
        }

        /// <summary>True if the window's title bar would be at least partly on a connected screen.</summary>
        static bool IsOnScreen(Rectangle b)
        {
            if (b.Width <= 0 || b.Height <= 0) return false;
            var titleBar = new Rectangle(b.X, b.Y, b.Width, 40);
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(titleBar)) return true;
            return false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveConfig(); // remember window position and size
            if (runner != null) runner.Stop();
            base.OnFormClosing(e);
        }
    }

    class EditDialog : Form
    {
        readonly PointEditor editor = new PointEditor();
        public ClickPoint Result;

        public EditDialog(ClickPoint point)
        {
            Text = "Edit point";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            editor.Value = point;
            var ok = new Button { Text = "OK", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += delegate
            {
                var p = editor.Read();
                if (p == null) return;
                Result = p;
                DialogResult = DialogResult.OK;
            };
            AcceptButton = ok;
            CancelButton = cancel;

            var bar = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill
            };
            bar.Controls.Add(cancel);
            bar.Controls.Add(ok);

            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2 };
            root.Controls.Add(editor, 0, 0);
            root.Controls.Add(bar, 0, 1);
            Controls.Add(root);
        }
    }
}
