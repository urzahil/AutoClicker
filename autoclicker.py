"""Multi-point autoclicker for Windows. No dependencies (Python 3 + tkinter).

Each point has its own location, interval, random offset, mouse button and
click type. After every click the next delay is  interval + uniform(-offset, +offset).
Global hotkey F6 starts/stops clicking, even when the window is not focused.
"""
import ctypes
import heapq
import json
import random
import threading
import time
import tkinter as tk
from dataclasses import dataclass, asdict
from tkinter import filedialog, messagebox, ttk

# ---------------------------------------------------------------- Win32 ----
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # physical pixel coordinates
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

user32 = ctypes.windll.user32
try:
    ctypes.windll.winmm.timeBeginPeriod(1)  # 1 ms timer resolution
except Exception:
    pass


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


EVENTS = {  # button -> (down flag, up flag)
    "Left": (0x0002, 0x0004),
    "Right": (0x0008, 0x0010),
    "Middle": (0x0020, 0x0040),
}
VK_F6 = 0x75


def cursor_pos():
    p = POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def click(x, y, button, double):
    user32.SetCursorPos(int(x), int(y))
    down, up = EVENTS[button]
    for _ in range(2 if double else 1):
        user32.mouse_event(down, 0, 0, 0, 0)
        user32.mouse_event(up, 0, 0, 0, 0)
        if double:
            time.sleep(0.02)


def fmt_interval(ms):
    mins, rest = divmod(ms, 60000)
    secs, ms = divmod(rest, 1000)
    return " ".join(f"{v}{u}" for v, u in ((mins, "m"), (secs, "s"), (ms, "ms")) if v) or "0ms"


# ------------------------------------------------------------------ Model ---
@dataclass
class ClickPoint:
    x: int = 0
    y: int = 0
    interval_ms: int = 1000
    offset_ms: int = 0
    button: str = "Left"
    click_type: str = "Single"
    enabled: bool = True

    def next_delay(self):
        d = self.interval_ms + random.uniform(-self.offset_ms, self.offset_ms)
        return max(1.0, d) / 1000.0


class Clicker(threading.Thread):
    """Runs every enabled point on its own independent schedule."""

    def __init__(self, points, on_stop):
        super().__init__(daemon=True)
        self.points = [ClickPoint(**asdict(p)) for p in points if p.enabled]
        self.stop_event = threading.Event()
        self.on_stop = on_stop

    def run(self):
        start = time.perf_counter() + 0.5
        heap = [(start, i) for i in range(len(self.points))]
        heapq.heapify(heap)
        while heap and not self.stop_event.is_set():
            due, i = heap[0]
            wait = due - time.perf_counter()
            if wait > 0:
                if self.stop_event.wait(wait):
                    break
            p = self.points[i]
            click(p.x, p.y, p.button, p.click_type == "Double")
            heapq.heapreplace(heap, (time.perf_counter() + p.next_delay(), i))
        self.on_stop()


# --------------------------------------------------------------------- UI ---
class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("AutoClicker")
        self.geometry("760x520")
        self.minsize(700, 480)
        self.points: list[ClickPoint] = []
        self.runner = None
        self.f6_was_down = False
        self._loading = False

        self._build()
        for k, val in (("x", 500), ("y", 500), ("min", 0), ("sec", 1), ("ms", 0),
                       ("offset", 0), ("button", "Left"), ("type", "Single")):
            self.v[k].set(val)  # defaults for new points
        self._add_point()  # start with one point
        self.after(50, self._poll_hotkey)
        self.protocol("WM_DELETE_WINDOW", self._close)

    # ---- layout
    def _build(self):
        top = ttk.Frame(self, padding=8)
        top.pack(fill="x")
        self.start_btn = ttk.Button(top, text="Start (F6)", command=self.toggle)
        self.start_btn.pack(side="left")
        self.status = ttk.Label(top, text="Stopped")
        self.status.pack(side="left", padx=12)
        ttk.Button(top, text="Save…", command=self._save).pack(side="right")
        ttk.Button(top, text="Load…", command=self._load).pack(side="right", padx=4)

        cols = ("#", "On", "X", "Y", "Button", "Type", "Interval", "± Offset (ms)")
        self.tree = ttk.Treeview(self, columns=cols, show="headings", height=9,
                                 selectmode="browse")
        for c, w in zip(cols, (35, 40, 70, 70, 80, 80, 110, 110)):
            self.tree.heading(c, text=c)
            self.tree.column(c, width=w, anchor="center")
        self.tree.pack(fill="both", expand=True, padx=8)
        self.tree.bind("<<TreeviewSelect>>", self._on_select)
        self.tree.bind("<Double-1>", self._toggle_enabled)

        # editor
        ed = ttk.LabelFrame(self, text="Selected point", padding=8)
        ed.pack(fill="x", padx=8, pady=8)
        self.v = {
            "x": tk.StringVar(), "y": tk.StringVar(),
            "min": tk.StringVar(), "sec": tk.StringVar(), "ms": tk.StringVar(),
            "offset": tk.StringVar(),
            "button": tk.StringVar(), "type": tk.StringVar(),
        }
        def entry(label, key, r, c):
            ttk.Label(ed, text=label).grid(row=r, column=c * 2, sticky="e", padx=(8, 4), pady=3)
            e = ttk.Entry(ed, textvariable=self.v[key], width=10)
            e.grid(row=r, column=c * 2 + 1, sticky="w")
            return e
        def combo(label, key, vals, r, c):
            ttk.Label(ed, text=label).grid(row=r, column=c * 2, sticky="e", padx=(8, 4), pady=3)
            cb = ttk.Combobox(ed, textvariable=self.v[key], values=vals, width=9, state="readonly")
            cb.grid(row=r, column=c * 2 + 1, sticky="w")
            return cb

        def interval_fields(r, c):
            ttk.Label(ed, text="Interval").grid(row=r, column=c * 2, sticky="e", padx=(8, 4), pady=3)
            fr = ttk.Frame(ed)
            fr.grid(row=r, column=c * 2 + 1, sticky="w")
            es = []
            for key, unit in (("min", "min"), ("sec", "s"), ("ms", "ms")):
                e = ttk.Entry(fr, textvariable=self.v[key], width=4)
                e.pack(side="left")
                ttk.Label(fr, text=unit).pack(side="left", padx=(2, 6))
                es.append(e)
            return es

        self.editors = [
            entry("X", "x", 0, 0), entry("Y", "y", 0, 1),
            *interval_fields(1, 0), entry("± Random offset (ms)", "offset", 1, 1),
            combo("Button", "button", list(EVENTS), 0, 2),
            combo("Click type", "type", ["Single", "Double"], 1, 2),
        ]
        self.pick_btn = ttk.Button(ed, text="Pick location", command=self._pick)
        self.pick_btn.grid(row=0, column=6, padx=12)
        for var in self.v.values():  # edits apply to the selected point immediately
            var.trace_add("write", self._live_apply)

        bar = ttk.Frame(self, padding=(8, 0, 8, 8))
        bar.pack(fill="x")
        self.list_buttons = [
            ttk.Button(bar, text="Add point", command=self._add_point),
            ttk.Button(bar, text="Duplicate", command=self._duplicate),
            ttk.Button(bar, text="Remove", command=self._remove),
            ttk.Button(bar, text="Move up", command=lambda: self._move(-1)),
            ttk.Button(bar, text="Move down", command=lambda: self._move(1)),
        ]
        for b in self.list_buttons:
            b.pack(side="left", padx=(0, 4))
        ttk.Label(bar, text="Double-click a row to enable/disable it").pack(side="right")

    # ---- list handling
    def _selected(self):
        sel = self.tree.selection()
        return int(sel[0]) if sel else None

    @staticmethod
    def _row(i, p):
        return (i + 1, "✔" if p.enabled else "—", p.x, p.y, p.button,
                p.click_type, fmt_interval(p.interval_ms), p.offset_ms)

    def _refresh(self, select=None):
        self.tree.delete(*self.tree.get_children())
        for i, p in enumerate(self.points):
            self.tree.insert("", "end", iid=str(i), values=self._row(i, p))
        if select is not None and 0 <= select < len(self.points):
            self.tree.selection_set(str(select))

    def _on_select(self, _=None):
        i = self._selected()
        if i is None:
            return
        self._loading = True  # don't write loaded values straight back
        p = self.points[i]
        self.v["x"].set(p.x); self.v["y"].set(p.y)
        mins, rest = divmod(p.interval_ms, 60000)
        secs, ms = divmod(rest, 1000)
        self.v["min"].set(mins); self.v["sec"].set(secs); self.v["ms"].set(ms)
        self.v["offset"].set(p.offset_ms)
        self.v["button"].set(p.button); self.v["type"].set(p.click_type)
        self._loading = False

    def _add_point(self):
        try:
            p = self._read_form()
        except ValueError:
            self._form_error()
            return
        self.points.append(p)
        self._refresh(len(self.points) - 1)

    def _duplicate(self):
        i = self._selected()
        if i is not None:
            self.points.insert(i + 1, ClickPoint(**asdict(self.points[i])))
            self._refresh(i + 1)

    def _remove(self):
        i = self._selected()
        if i is not None:
            del self.points[i]
            self._refresh(min(i, len(self.points) - 1))

    def _move(self, d):
        i = self._selected()
        if i is not None and 0 <= i + d < len(self.points):
            self.points[i], self.points[i + d] = self.points[i + d], self.points[i]
            self._refresh(i + d)

    def _toggle_enabled(self, _=None):
        if self.runner:
            return
        i = self._selected()
        if i is not None:
            self.points[i].enabled = not self.points[i].enabled
            self._refresh(i)

    def _read_form(self):
        """Build a ClickPoint from the editor fields; ValueError if invalid."""
        x, y = int(self.v["x"].get()), int(self.v["y"].get())
        mins, secs, ms = (int(self.v[k].get() or 0) for k in ("min", "sec", "ms"))
        offset = int(self.v["offset"].get() or 0)
        interval = (mins * 60 + secs) * 1000 + ms
        if min(mins, secs, ms) < 0 or interval < 1 or offset < 0:
            raise ValueError
        return ClickPoint(x=x, y=y, interval_ms=interval, offset_ms=offset,
                          button=self.v["button"].get(), click_type=self.v["type"].get())

    def _form_error(self):
        messagebox.showerror("Invalid value", "X, Y, interval and offset must be whole "
                             "numbers (interval at least 1 ms, offset ≥ 0).")

    def _live_apply(self, *_):
        i = self._selected()
        if i is None or self._loading:
            return
        try:
            new = self._read_form()  # half-typed values are ignored until valid
        except ValueError:
            return
        p = self.points[i]
        new.enabled = p.enabled
        self.points[i] = new
        self.tree.item(str(i), values=self._row(i, new))

    def _pick(self):
        """Cover all screens with a faint overlay; the next left click picks the spot."""
        gm = user32.GetSystemMetrics
        vx, vy, vw, vh = gm(76), gm(77), gm(78), gm(79)  # virtual screen
        ov = tk.Toplevel(self)
        ov.overrideredirect(True)
        ov.geometry(f"{vw}x{vh}{vx:+d}{vy:+d}")
        ov.attributes("-topmost", True)
        ov.attributes("-alpha", 0.25)
        ov.configure(bg="black", cursor="crosshair")
        tk.Label(ov, text="Left-click to pick a location  (Esc to cancel)",
                 fg="white", bg="black", font=("Segoe UI", 18)).place(relx=0.5, rely=0.1,
                                                                      anchor="center")

        def done(pick):
            ov.destroy()
            if pick:
                x, y = cursor_pos()
                self.v["x"].set(x); self.v["y"].set(y)

        ov.bind("<Button-1>", lambda e: done(True))
        ov.bind("<Escape>", lambda e: done(False))
        ov.focus_force()

    # ---- run control
    def toggle(self):
        if self.runner:
            self.runner.stop_event.set()
            return
        if not any(p.enabled for p in self.points):
            messagebox.showinfo("Nothing to do", "Add or enable at least one point.")
            return
        self.runner = Clicker(self.points, lambda: self.after(0, self._stopped))
        self.runner.start()
        self.start_btn.config(text="Stop (F6)")
        self.status.config(text="Running…")
        self._lock(True)

    def _stopped(self):
        self.runner = None
        self.start_btn.config(text="Start (F6)")
        self.status.config(text="Stopped")
        self._lock(False)

    def _lock(self, locked):
        state = "disabled" if locked else "normal"
        for w in self.editors + [self.pick_btn] + self.list_buttons:
            try:
                w.config(state="disabled" if locked else
                         ("readonly" if isinstance(w, ttk.Combobox) else "normal"))
            except tk.TclError:
                w.state(["disabled"] if locked else ["!disabled"])

    def _poll_hotkey(self):
        down = bool(user32.GetAsyncKeyState(VK_F6) & 0x8000)
        if down and not self.f6_was_down:
            self.toggle()
        self.f6_was_down = down
        self.after(50, self._poll_hotkey)

    # ---- persistence
    def _save(self):
        f = filedialog.asksaveasfilename(defaultextension=".json",
                                         filetypes=[("JSON", "*.json")])
        if f:
            with open(f, "w") as fh:
                json.dump([asdict(p) for p in self.points], fh, indent=2)

    def _load(self):
        f = filedialog.askopenfilename(filetypes=[("JSON", "*.json")])
        if not f:
            return
        try:
            with open(f) as fh:
                self.points = [ClickPoint(**d) for d in json.load(fh)]
        except Exception as e:
            messagebox.showerror("Load failed", str(e))
            return
        self._refresh(0 if self.points else None)

    def _close(self):
        if self.runner:
            self.runner.stop_event.set()
        self.destroy()


if __name__ == "__main__":
    App().mainloop()

