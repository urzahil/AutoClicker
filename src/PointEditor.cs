using System;
using System.Drawing;
using System.Windows.Forms;

namespace AutoClicker
{
    /// <summary>Fields for one point; used for the "New point" panel and the edit dialog.</summary>
    class PointEditor : UserControl
    {
        readonly NumericUpDown x = Num(-32768, 65535), y = Num(-32768, 65535);
        readonly NumericUpDown min = Num(0, 1440), sec = Num(0, 59), offset = Num(0, 3600000);
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
            t.Controls.Add(Row(min, Lbl("min"), sec, Lbl("sec")), 1, 1);
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

        /// <summary>Current field values; null (with a message shown) if the interval is 0.</summary>
        public ClickPoint Read()
        {
            int interval = ((int)min.Value * 60 + (int)sec.Value) * 1000;
            if (interval < 1000)
            {
                MessageBox.Show(FindForm(), "The interval must be at least 1 sec.",
                    "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            var p = new ClickPoint();
            p.X = (int)x.Value; p.Y = (int)y.Value;
            p.IntervalMs = interval; p.OffsetMs = (int)offset.Value;
            p.Button = (string)button.SelectedItem;
            p.ClickType = (string)type.SelectedItem;
            return p;
        }

        public ClickPoint Value
        {
            set
            {
                x.Value = Clamp(x, value.X); y.Value = Clamp(y, value.Y);
                int secs = value.IntervalMs / 1000;
                min.Value = Math.Min(min.Maximum, secs / 60); sec.Value = secs % 60;
                offset.Value = Clamp(offset, value.OffsetMs);
                button.SelectedItem = value.Button;
                type.SelectedItem = value.ClickType;
            }
        }

        public void SetLocked(bool locked)
        {
            foreach (Control c in new Control[] { x, y, min, sec, offset, button, type, pick })
                c.Enabled = !locked;
        }
    }

    /// <summary>Full-screen faint overlay; the next left click picks the position.</summary>
    class PickOverlay : Form
    {
        readonly Action<Point> callback;

        public static void Pick(Form owner, Action<Point> callback)
        {
            new PickOverlay(callback).Show(owner);
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

            var l = new Label();
            l.Text = "Left-click to pick a location  (Esc to cancel)";
            l.ForeColor = Color.White;
            l.Font = new Font("Segoe UI", 18);
            l.AutoSize = true;
            l.Location = new Point(Width / 2 - 280, Height / 10);
            Controls.Add(l);
            l.MouseDown += OnMouse;
            MouseDown += OnMouse;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            Shown += delegate { Activate(); };
        }

        void OnMouse(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var p = Cursor.Position;
            Close();
            callback(p);
        }
    }
}
