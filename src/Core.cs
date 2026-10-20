using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoClicker
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);

        public static bool IsDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    }

    class ClickPoint
    {
        public int X = 500;
        public int Y = 500;
        public int IntervalMs = 1000;
        public int OffsetMs = 0;
        public string Button = "Left";
        public string ClickType = "Single";
        public bool Enabled = true;

        public ClickPoint Clone() { return (ClickPoint)MemberwiseClone(); }

        public double NextDelaySeconds(Random rnd)
        {
            double d = IntervalMs + (rnd.NextDouble() * 2 - 1) * OffsetMs;
            return Math.Max(1.0, d) / 1000.0;
        }

        public static string FormatInterval(int ms)
        {
            int mins = ms / 60000, secs = (ms % 60000) / 1000, rest = ms % 1000;
            var parts = new List<string>();
            if (mins > 0) parts.Add(mins + "m");
            if (secs > 0) parts.Add(secs + "s");
            if (rest > 0) parts.Add(rest + "ms");
            return parts.Count == 0 ? "0s" : string.Join(" ", parts.ToArray());
        }
    }

    /// <summary>Runs every enabled point on its own independent schedule.</summary>
    class Clicker
    {
        readonly List<ClickPoint> points = new List<ClickPoint>();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly AutoResetEvent changed = new AutoResetEvent(false);
        readonly Action onStopped;
        readonly Thread thread;
        readonly Random rnd = new Random();

        /// <summary>
        /// Keeps references to the points (not copies) so toggling Enabled takes effect while
        /// running; call NotifyChanged() after toggling. Other edits are locked while running.
        /// </summary>
        public Clicker(IEnumerable<ClickPoint> source, Action onStopped)
        {
            points.AddRange(source);
            this.onStopped = onStopped;
            thread = new Thread(Run);
            thread.IsBackground = true;
        }

        public void Start() { thread.Start(); }
        public void Stop() { stop.Set(); }
        public void NotifyChanged() { changed.Set(); }

        void Run()
        {
            var sw = Stopwatch.StartNew();
            var due = new double[points.Count];
            for (int i = 0; i < due.Length; i++)
                due[i] = points[i].Enabled ? 0.5 : double.PositiveInfinity;
            var handles = new WaitHandle[] { stop, changed };

            while (!stop.WaitOne(0))
            {
                // pick up points that were enabled/disabled since the last pass
                double now = sw.Elapsed.TotalSeconds;
                for (int i = 0; i < due.Length; i++)
                {
                    if (!points[i].Enabled) due[i] = double.PositiveInfinity;
                    else if (double.IsPositiveInfinity(due[i])) due[i] = now + 0.2;
                }

                int next = 0;
                for (int i = 1; i < due.Length; i++) if (due[i] < due[next]) next = i;

                int timeout = Timeout.Infinite;
                if (due.Length > 0 && !double.IsPositiveInfinity(due[next]))
                {
                    double wait = due[next] - now;
                    if (wait <= 0) timeout = 0;
                    else timeout = (int)Math.Ceiling(wait * 1000);
                }
                if (timeout != 0)
                {
                    if (WaitHandle.WaitAny(handles, timeout) == 0) break;
                    continue; // woke up early, or the enabled set changed: re-evaluate
                }

                var p = points[next];
                Click(p);
                due[next] = sw.Elapsed.TotalSeconds + p.NextDelaySeconds(rnd);
            }
            onStopped();
        }
        static void Click(ClickPoint p)
        {
            uint down, up;
            switch (p.Button)
            {
                case "Right": down = 0x0008; up = 0x0010; break;
                case "Middle": down = 0x0020; up = 0x0040; break;
                default: down = 0x0002; up = 0x0004; break;
            }
            // Apps hit-test a click at the cursor position they last processed, and many ignore
            // a press/release pair with no gap, so let the move settle and hold the button briefly.
            Native.SetCursorPos(p.X, p.Y);
            Thread.Sleep(30);
            int n = p.ClickType == "Double" ? 2 : 1;
            for (int i = 0; i < n; i++)
            {
                Native.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(40);
                Native.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                if (i < n - 1) Thread.Sleep(60);
            }
        }
    }
}
