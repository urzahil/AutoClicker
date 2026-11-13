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
        public const string Version = "2.3.0";
    }

    static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);

        [StructLayout(LayoutKind.Sequential)]
        struct LastInputInfo { public uint Size; public uint Time; }

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
        readonly bool keepAwake;

        /// <summary>
        /// Keeps references to the points (not copies) so toggling Enabled takes effect while
        /// running; call NotifyChanged() after toggling. Other edits are locked while running.
        /// </summary>
        public Clicker(IEnumerable<ClickPoint> source, bool keepAwake, Action onStopped)
        {
            points.AddRange(source);
            this.keepAwake = keepAwake;
            this.onStopped = onStopped;
            thread = new Thread(Run);
            thread.IsBackground = true;
        }

        public void Start() { thread.Start(); }
        public void Stop() { stop.Set(); }
        public void NotifyChanged() { changed.Set(); }

        void Run()
        {
            if (keepAwake) KeepAwake.Begin();
            try { Loop(); }
            finally
            {
                if (keepAwake) KeepAwake.End();
                onStopped();
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

            while (!stop.WaitOne(0))
            {
                // pick up points that were enabled/disabled since the last pass
                double now = sw.Elapsed.TotalSeconds;
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
                Click(p);
                due[next] = sw.Elapsed.TotalSeconds + p.NextDelaySeconds(rnd);
            }
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
            Native.MoveTo(p.X, p.Y);
            Thread.Sleep(30);
            int n = p.ClickType == "Double" ? 2 : 1;
            for (int i = 0; i < n; i++)
            {
                Native.SendMouse(down, 0, 0);
                Thread.Sleep(40);
                Native.SendMouse(up, 0, 0);
                if (i < n - 1) Thread.Sleep(60);
            }
        }
    }
}
