using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

// shown in the exe's file properties
[assembly: AssemblyTitle("Snap")]
[assembly: AssemblyProduct("Snap")]
[assembly: AssemblyVersion(Snap.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(Snap.AppInfo.Version + ".0")]
[assembly: AssemblyInformationalVersion(Snap.AppInfo.Version)]

namespace Snap
{
    static class AppInfo
    {
        /// <summary>The release version: change it here (and only here) for each release.</summary>
        public const string Version = "2.3.1";
    }

    static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint p);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);

        [StructLayout(LayoutKind.Sequential)]
        struct LastInputInfo { public uint Size; public uint Time; }

        [StructLayout(LayoutKind.Sequential)]
        struct CursorPoint { public int X, Y; }

        /// <summary>Current cursor position in screen pixels; false if it can't be read.</summary>
        public static bool CursorPos(out int x, out int y)
        {
            CursorPoint p;
            bool ok = GetCursorPos(out p);
            x = p.X; y = p.Y;
            return ok;
        }

        // INPUT with only the MOUSEINPUT member of its union: MOUSEINPUT is the union's largest
        // member, and its pointer-sized field gives the same layout and size on x86 and x64.
        [StructLayout(LayoutKind.Sequential)]
        struct Input { public uint Type; public MouseInput Mouse; }

        [StructLayout(LayoutKind.Sequential)]
        struct MouseInput
        {
            public int Dx, Dy;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }

        public const uint MouseMove = 0x0001, MouseAbsolute = 0x8000, MouseVirtualDesk = 0x4000;
        const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

        /// <summary>Injects one mouse event through SendInput; false if it was blocked.</summary>
        public static bool SendMouse(uint flags, int dx, int dy)
        {
            var input = new Input[1];
            input[0].Type = 0; // INPUT_MOUSE
            input[0].Mouse.Dx = dx;
            input[0].Mouse.Dy = dy;
            input[0].Mouse.Flags = flags;
            return SendInput(1, input, Marshal.SizeOf(typeof(Input))) == 1;
        }

        /// <summary>
        /// Moves the cursor to a screen pixel as real mouse input, so the target app receives a
        /// mouse-move and hit-tests the following click at the new position (SetCursorPos only
        /// jumps the cursor, and apps that track the pointer through their own input never see it).
        /// </summary>
        public static bool MoveTo(int x, int y)
        {
            return SendMouse(MouseMove | MouseAbsolute | MouseVirtualDesk,
                Normalize(x, GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmCxVirtualScreen)),
                Normalize(y, GetSystemMetrics(SmYVirtualScreen), GetSystemMetrics(SmCyVirtualScreen)));
        }

        // Absolute coordinates span 0..65535 across the virtual desktop and Windows maps them back
        // with floor(n * size / 65536), so round up to land exactly on the requested pixel.
        static int Normalize(int v, int origin, int size)
        {
            if (size <= 0) return 0;
            long n = ((long)(v - origin) * 65536 + size - 1) / size;
            return (int)Math.Max(0, Math.Min(65535, n));
        }

        public static bool IsDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        /// <summary>Milliseconds since the last keyboard or mouse input (real or injected).</summary>
        public static uint IdleMs()
        {
            var info = new LastInputInfo();
            info.Size = (uint)Marshal.SizeOf(typeof(LastInputInfo));
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.Time);
        }
    }

    /// <summary>
    /// Keeps the PC from sleeping, turning the display off or locking for inactivity while the
    /// clicker runs. Execution state is per thread, so Begin/End must run on the clicker thread.
    /// </summary>
    static class KeepAwake
    {
        const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;
        // The domain lock fires after 15 minutes without input. Nudge after about half that
        // (7 min 30 s, re-randomised by up to ±20 s each time), checked once a minute, so the
        // longest possible gap is 7:50 + 1:00 = 8:50 — well inside the limit.
        public const double CheckSeconds = 60;
        const int BaseIdleMs = 450000, JitterMs = 20000;

        /// <summary>Picks the idle time that must pass before the next nudge.</summary>
        public static int NextIdleThresholdMs(Random rnd)
        {
            return BaseIdleMs + rnd.Next(-JitterMs, JitterMs + 1);
        }

        public static void Begin() { Native.SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired); }
        public static void End() { Native.SetThreadExecutionState(EsContinuous); }

        /// <summary>
        /// Sends a 1-pixel move and back if there has been no input for thresholdMs. Injected input
        /// resets the inactivity timer behind the "machine inactivity limit" lock and screen saver,
        /// which SetThreadExecutionState alone does not. The cursor normally ends where it started
        /// (pointer acceleration can leave it a pixel off; each click repositions it anyway).
        /// </summary>
        /// <returns>true if a nudge was sent.</returns>
        public static bool NudgeIfIdle(int thresholdMs)
        {
            if (Native.IdleMs() < (uint)thresholdMs) return false;
            Native.SendMouse(Native.MouseMove, 1, 0);
            Native.SendMouse(Native.MouseMove, -1, 0);
            return true;
        }
    }

    class ClickPoint
    {
        public const int MinIntervalMs = 50;
        public const int MaxIntervalMs = 1440 * 60000 + 59999; // what the editor can show
        public const int MaxOffsetMs = 3600000;
        public const int MinCoord = -32768, MaxCoord = 65535;
        public const int MaxJitterPx = 500, MaxLabelLength = 40;
        static readonly string[] Buttons = { "Left", "Right", "Middle" };
        static readonly string[] ClickTypes = { "Single", "Double" };

        public int X = 500;
        public int Y = 500;
        public int IntervalMs = 1000;
        public int OffsetMs = 0;
        public int JitterPx = 0; // each click lands at a random spot within this radius of X, Y
        public string Label = "";
        public string Button = "Left";
        public string ClickType = "Single";
        public bool Enabled = true;

        public ClickPoint Clone() { return (ClickPoint)MemberwiseClone(); }

        static string Canon(string value, string[] allowed)
        {
            if (value != null)
                foreach (var a in allowed)
                    if (string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase)) return a;
            return allowed[0];
        }

        /// <summary>"left", " RIGHT " etc. become "Left", "Right"; anything unknown becomes "Left".</summary>
        public static string CanonButton(string value) { return Canon(value, Buttons); }

        /// <summary>Same for the click type; unknown becomes "Single".</summary>
        public static string CanonClickType(string value) { return Canon(value, ClickTypes); }

        /// <summary>Forces every field into a range the app can show and click with.</summary>
        public void Normalize()
        {
            X = Math.Max(MinCoord, Math.Min(MaxCoord, X));
            Y = Math.Max(MinCoord, Math.Min(MaxCoord, Y));
            IntervalMs = Math.Max(MinIntervalMs, Math.Min(MaxIntervalMs, IntervalMs));
            OffsetMs = Math.Max(0, Math.Min(MaxOffsetMs, OffsetMs));
            JitterPx = Math.Max(0, Math.Min(MaxJitterPx, JitterPx));
            Label = (Label ?? "").Trim();
            if (Label.Length > MaxLabelLength) Label = Label.Substring(0, MaxLabelLength);
            Button = CanonButton(Button);
            ClickType = CanonClickType(ClickType);
        }

        static int ReadInt(IDictionary<string, object> d, string key, int fallback)
        {
            object o;
            if (!d.TryGetValue(key, out o) || o == null) return fallback;
            try { return Convert.ToInt32(o); }
            catch (Exception) { return fallback; }
        }

        static string ReadString(IDictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) ? o as string : null;
        }

        /// <summary>
        /// Builds a point from a JSON object. Missing or unreadable fields fall back to the
        /// defaults, unknown fields are ignored and out-of-range values are clamped, so files
        /// from other versions (or edited by hand) can't produce a point that breaks the UI.
        /// </summary>
        public static ClickPoint FromDictionary(IDictionary<string, object> d)
        {
            if (d == null || !d.ContainsKey("x") || !d.ContainsKey("y"))
                throw new FormatException("A point is missing its \"x\" or \"y\" value.");
            var p = new ClickPoint();
            p.X = ReadInt(d, "x", p.X);
            p.Y = ReadInt(d, "y", p.Y);
            p.IntervalMs = ReadInt(d, "interval_ms", p.IntervalMs);
            p.OffsetMs = ReadInt(d, "offset_ms", p.OffsetMs);
            p.JitterPx = ReadInt(d, "jitter_px", p.JitterPx);
            p.Label = ReadString(d, "label");
            p.Button = ReadString(d, "button");
            p.ClickType = ReadString(d, "click_type");
            object en;
            if (d.TryGetValue("enabled", out en) && en != null)
            {
                try { p.Enabled = Convert.ToBoolean(en); }
                catch (Exception) { }
            }
            p.Normalize();
            return p;
        }

        // Same JSON layout as the original Python version, so saved setups still load.
        public Dictionary<string, object> ToDictionary()
        {
            var d = new Dictionary<string, object>();
            d["x"] = X; d["y"] = Y;
            d["interval_ms"] = IntervalMs; d["offset_ms"] = OffsetMs;
            d["jitter_px"] = JitterPx; d["label"] = Label;
            d["button"] = Button; d["click_type"] = ClickType; d["enabled"] = Enabled;
            return d;
        }

        public double NextDelaySeconds(Random rnd)
        {
            double d = IntervalMs + (rnd.NextDouble() * 2 - 1) * OffsetMs;
            return Math.Max(MinIntervalMs, d) / 1000.0;
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
        readonly Action onBlocked;
        readonly Action onStopped;
        readonly Thread thread;
        readonly Random rnd = new Random();
        readonly bool keepAwake;
        readonly bool returnCursor;
        readonly int maxClicks;
        readonly int maxMinutes;
        bool blockedReported;
        volatile string stopReason;

        /// <summary>Why the run ended by itself ("click limit" / "time limit"); null if it was stopped manually.</summary>
        public string StopReason { get { return stopReason; } }

        /// <summary>
        /// Keeps references to the points (not copies) so toggling Enabled takes effect while
        /// running; call NotifyChanged() after toggling. Other edits are locked while running.
        /// onBlocked (at most once per run) and onStopped are called on the clicker thread.
        /// maxClicks / maxMinutes of 0 mean "no limit".
        /// </summary>
        public Clicker(IEnumerable<ClickPoint> source, bool keepAwake, bool returnCursor,
            int maxClicks, int maxMinutes, Action onBlocked, Action onStopped)
        {
            points.AddRange(source);
            this.keepAwake = keepAwake;
            this.returnCursor = returnCursor;
            this.maxClicks = maxClicks;
            this.maxMinutes = maxMinutes;
            this.onBlocked = onBlocked;
            this.onStopped = onStopped;
            thread = new Thread(Run);
            thread.IsBackground = true;
        }

        public void Start() { thread.Start(); }

        // The events are disposed when the thread ends, and the UI can still call these
        // in the short gap before it learns that the clicker has stopped.
        public void Stop()
        {
            try { stop.Set(); } catch (ObjectDisposedException) { }
        }

        public void NotifyChanged()
        {
            try { changed.Set(); } catch (ObjectDisposedException) { }
        }

        /// <summary>Stops the clicker and waits (at most timeoutMs) for its thread to finish.</summary>
        public void StopAndWait(int timeoutMs)
        {
            Stop();
            if (thread.IsAlive && Thread.CurrentThread != thread) thread.Join(timeoutMs);
        }

        void Run()
        {
            // 1 ms timer resolution for accurate intervals, only while clicking: keeping it
            // requested forever would cost battery while Snap idles in the tray.
            Native.timeBeginPeriod(1);
            if (keepAwake) KeepAwake.Begin();
            try { Loop(); }
            finally
            {
                if (keepAwake) KeepAwake.End();
                Native.timeEndPeriod(1);
                try { onStopped(); }
                finally { stop.Dispose(); changed.Dispose(); }
            }
        }

        void Loop()
        {
            var sw = Stopwatch.StartNew();
            var due = new double[points.Count];
            for (int i = 0; i < due.Length; i++)
                due[i] = points[i].Enabled ? 0.5 : double.PositiveInfinity;
            double nudgeDue = keepAwake ? KeepAwake.CheckSeconds : double.PositiveInfinity;
            int idleThresholdMs = KeepAwake.NextIdleThresholdMs(rnd);
            var handles = new WaitHandle[] { stop, changed };
            double endAt = maxMinutes > 0 ? maxMinutes * 60.0 : double.PositiveInfinity;
            int clicks = 0;

            while (!stop.WaitOne(0))
            {
                // pick up points that were enabled/disabled since the last pass
                double now = sw.Elapsed.TotalSeconds;
                if (now >= endAt) { stopReason = "time limit"; break; }
                for (int i = 0; i < due.Length; i++)
                {
                    if (!points[i].Enabled) due[i] = double.PositiveInfinity;
                    else if (double.IsPositiveInfinity(due[i])) due[i] = now + 0.2;
                }

                if (now >= nudgeDue)
                {
                    if (KeepAwake.NudgeIfIdle(idleThresholdMs))
                        idleThresholdMs = KeepAwake.NextIdleThresholdMs(rnd); // fresh ±20 s for the next one
                    nudgeDue = now + KeepAwake.CheckSeconds;
                }

                int next = 0;
                for (int i = 1; i < due.Length; i++) if (due[i] < due[next]) next = i;

                double nextAt = due.Length > 0 ? Math.Min(due[next], nudgeDue) : nudgeDue;
                nextAt = Math.Min(nextAt, endAt);
                int timeout = Timeout.Infinite;
                if (!double.IsPositiveInfinity(nextAt))
                {
                    double wait = nextAt - now;
                    timeout = wait <= 0 ? 0 : (int)Math.Ceiling(wait * 1000);
                }
                if (timeout != 0 || due.Length == 0 || due[next] > now)
                {
                    if (WaitHandle.WaitAny(handles, timeout) == 0) break;
                    continue; // woke up early, the enabled set changed, or a nudge is due: re-evaluate
                }

                var p = points[next];
                if (!Click(p) && !blockedReported)
                {
                    blockedReported = true;
                    if (onBlocked != null) onBlocked();
                }
                // Schedule from the time the click was due, not from when it finished, so the
                // time Click() takes doesn't add drift; never schedule in the past.
                due[next] = Math.Max(sw.Elapsed.TotalSeconds, due[next] + p.NextDelaySeconds(rnd));
                clicks++;
                if (maxClicks > 0 && clicks >= maxClicks) { stopReason = "click limit"; break; }
            }
        }

        /// <summary>
        /// Clicks one point. Every pause also listens for Stop, so stopping doesn't have to wait
        /// out the sequence; a pressed button is always released. False if input was blocked.
        /// </summary>
        bool Click(ClickPoint p)
        {
            int oldX = 0, oldY = 0;
            bool restore = returnCursor && Native.CursorPos(out oldX, out oldY);
            uint down, up;
            switch (p.Button)
            {
                case "Right": down = 0x0008; up = 0x0010; break;
                case "Middle": down = 0x0020; up = 0x0040; break;
                default: down = 0x0002; up = 0x0004; break;
            }
            int tx = p.X, ty = p.Y;
            if (p.JitterPx > 0)
            {
                // uniform over a disc: the square root keeps the points from bunching at the centre
                double angle = rnd.NextDouble() * 2 * Math.PI, radius = p.JitterPx * Math.Sqrt(rnd.NextDouble());
                tx += (int)Math.Round(radius * Math.Cos(angle));
                ty += (int)Math.Round(radius * Math.Sin(angle));
            }
            bool ok = true, aborted = false;
            // Apps hit-test a click at the cursor position they last processed, and many ignore
            // a press/release pair with no gap, so let the move settle and hold the button briefly.
            if (!Native.MoveTo(tx, ty)) ok = false;
            else if (stop.WaitOne(30)) aborted = true;
            int n = p.ClickType == "Double" ? 2 : 1;
            for (int i = 0; i < n && ok && !aborted; i++)
            {
                bool pressed = false;
                try
                {
                    pressed = Native.SendMouse(down, 0, 0);
                    if (!pressed) { ok = false; break; }
                    if (stop.WaitOne(40)) aborted = true;
                }
                finally
                {
                    if (pressed) Native.SendMouse(up, 0, 0); // never leave the button stuck down
                }
                if (!aborted && i < n - 1 && stop.WaitOne(60)) aborted = true;
            }
            if (restore)
            {
                // give the app time to handle the release at the click position before moving away
                if (!aborted) stop.WaitOne(30);
                Native.MoveTo(oldX, oldY);
            }
            return ok;
        }
    }
}
