using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Snap
{
    /// <summary>
    /// Tracks whether a modal dialog, message box or the location picker is open, so the global
    /// hotkey doesn't start the clicker underneath it. Everything runs on the UI thread.
    /// </summary>
    static class ModalState
    {
        public static int Depth;

        public static DialogResult Show(Func<DialogResult> show)
        {
            Depth++;
            try { return show(); }
            finally { Depth--; }
        }

        public static DialogResult Message(IWin32Window owner, string text, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            Depth++;
            try { return MessageBox.Show(owner, text, caption, buttons, icon); }
            finally { Depth--; }
        }
    }

    /// <summary>Fields for one point; used for the "New point" panel and the edit dialog.</summary>
    class PointEditor : UserControl
    {
        readonly NumericUpDown x = Num(-32768, 65535), y = Num(-32768, 65535);
        readonly NumericUpDown min = Num(0, 1440), sec = Num(0, 59), ms = Num(0, 999), offset = Num(0, 3600000);
        readonly ComboBox button = Combo("Left", "Right", "Middle");
        readonly ComboBox type = Combo("Single", "Double");
        readonly Button pick = new Button();

        public PointEditor()
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var t = new TableLayoutPanel();
            t.AutoSize = true;
            t.ColumnCount = 6;
            t.RowCount = 2;
            Controls.Add(t);

            t.Controls.Add(Lbl("Location"), 0, 0);
            t.Controls.Add(Row(Lbl("X"), x, Lbl("Y"), y), 1, 0);
            t.Controls.Add(Lbl("Button"), 2, 0);
            t.Controls.Add(button, 3, 0);
            pick.Text = "Pick location";
            pick.AutoSize = true;
            pick.Click += delegate { PickLocation(); };
            t.Controls.Add(pick, 4, 0);
            t.SetColumnSpan(pick, 2);

            t.Controls.Add(Lbl("Interval"), 0, 1);
            sec.Width = 48; ms.Width = 56;
            t.Controls.Add(Row(min, Lbl("min"), sec, Lbl("sec"), ms, Lbl("ms")), 1, 1);
            t.Controls.Add(Lbl("Click type"), 2, 1);
            t.Controls.Add(type, 3, 1);
            t.Controls.Add(Lbl("± Random offset"), 4, 1);
            t.Controls.Add(Row(offset, Lbl("ms")), 5, 1);

            Value = new ClickPoint();
        }

        static NumericUpDown Num(int lo, int hi)
        {
            var n = new NumericUpDown();
            n.Minimum = lo; n.Maximum = hi;
            n.Width = 65;
            n.Anchor = AnchorStyles.Left;
            return n;
        }

        static ComboBox Combo(params string[] items)
        {
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(items);
            c.Width = 80;
            c.Anchor = AnchorStyles.Left;
            return c;
        }

        static Label Lbl(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Right;
            l.Margin = new Padding(8, 6, 4, 6);
            l.TextAlign = ContentAlignment.MiddleRight;
            return l;
        }

        static Control Row(params Control[] items)
        {
            var f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.WrapContents = false;
            f.Margin = Padding.Empty;
            f.Anchor = AnchorStyles.Left;
            foreach (var c in items)
            {
                if (c is Label) c.Margin = new Padding(2, 0, 6, 0);
                f.Controls.Add(c);
            }
            return f;
        }

        void PickLocation()
        {
            PickOverlay.Pick(FindForm(), delegate(Point p)
            {
                x.Value = Clamp(x, p.X);
                y.Value = Clamp(y, p.Y);
            });
        }

        static decimal Clamp(NumericUpDown n, int v)
        {
            return Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        }

        /// <summary>Current field values; null (with a message shown) if the interval is too short.</summary>
        public ClickPoint Read()
        {
            int interval = ((int)min.Value * 60 + (int)sec.Value) * 1000 + (int)ms.Value;
            if (interval < ClickPoint.MinIntervalMs)
            {
                ModalState.Message(FindForm(),
                    "The interval must be at least " + ClickPoint.MinIntervalMs + " ms.",
                    "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            var p = new ClickPoint();
            p.X = (int)x.Value; p.Y = (int)y.Value;
            p.IntervalMs = interval; p.OffsetMs = (int)offset.Value;
            p.Button = ClickPoint.CanonButton((string)button.SelectedItem);
            p.ClickType = ClickPoint.CanonClickType((string)type.SelectedItem);
            return p;
        }

        public ClickPoint Value
        {
            set
            {
                x.Value = Clamp(x, value.X); y.Value = Clamp(y, value.Y);
                int total = Math.Max(0, value.IntervalMs);
                min.Value = Clamp(min, total / 60000);
                sec.Value = Clamp(sec, (total % 60000) / 1000);
                ms.Value = Clamp(ms, total % 1000);
                offset.Value = Clamp(offset, value.OffsetMs);
                button.SelectedItem = ClickPoint.CanonButton(value.Button);
                type.SelectedItem = ClickPoint.CanonClickType(value.ClickType);
            }
        }

        public void SetLocked(bool locked)
        {
            foreach (Control c in new Control[] { x, y, min, sec, ms, offset, button, type, pick })
                c.Enabled = !locked;
        }
    }

    /// <summary>Full-screen faint overlay; the next left click picks the position.</summary>
    class PickOverlay : Form
    {
        readonly Action<Point> callback;
        // Snap's own windows are made invisible while picking, so they can't cover the target.
        readonly List<KeyValuePair<Form, double>> hidden = new List<KeyValuePair<Form, double>>();
        readonly Font bannerFont = new Font("Segoe UI", 18);
        readonly Label banner = new Label();
        readonly Label coords = new Label();

        public static void Pick(Form owner, Action<Point> callback)
        {
            var overlay = new PickOverlay(callback);
            for (Form f = owner; f != null; f = f.Owner)
            {
                overlay.hidden.Add(new KeyValuePair<Form, double>(f, f.Opacity));
                f.Opacity = 0;
            }
            ModalState.Depth++;
            overlay.Show(owner);
        }

        PickOverlay(Action<Point> callback)
        {
            this.callback = callback;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            BackColor = Color.Black;
            Opacity = 0.25;
            TopMost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            KeyPreview = true;

            banner.Text = "Left-click to pick a location  (Esc to cancel)";
            banner.ForeColor = Color.White;
            banner.Font = bannerFont;
            banner.AutoSize = true;
            Controls.Add(banner);
            coords.ForeColor = Color.White;
            coords.Font = bannerFont;
            coords.AutoSize = true;
            Controls.Add(coords);

            banner.MouseDown += OnMouse;
            coords.MouseDown += OnMouse;
            MouseDown += OnMouse;
            banner.MouseMove += delegate { TrackCursor(); };
            MouseMove += delegate { TrackCursor(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            Shown += delegate { PositionBanner(); TrackCursor(); Activate(); };
            FormClosed += delegate { Restore(); };
        }

        /// <summary>Centres the instructions on the screen the cursor is on, not on the whole desktop.</summary>
        void PositionBanner()
        {
            var screen = Screen.FromPoint(Cursor.Position).Bounds;
            banner.Location = new Point(
                screen.Left - Left + (screen.Width - banner.PreferredSize.Width) / 2,
                screen.Top - Top + screen.Height / 10);
        }

        /// <summary>Shows the live (X, Y) next to the cursor.</summary>
        void TrackCursor()
        {
            var c = Cursor.Position;
            var at = PointToClient(c);
            coords.Text = c.X + ", " + c.Y;
            coords.Location = new Point(at.X + 20, at.Y + 24);
        }

        void Restore()
        {
            foreach (var h in hidden) h.Key.Opacity = h.Value;
            hidden.Clear();
            ModalState.Depth--;
        }

        void OnMouse(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var p = Cursor.Position;
            Close();
            callback(p);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) bannerFont.Dispose();
        }
    }
}
