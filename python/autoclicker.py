"""Multi-point autoclicker for Windows. No dependencies (Python 3 + tkinter).

Each point has its own location, interval, random offset, mouse button and
click type. After every click the next delay is  interval + uniform(-offset, +offset).
A global hotkey (F6 by default, changeable) starts/stops clicking, even when the
window is not focused.
"""
import ctypes
import heapq
import json
import os
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
CONFIG = os.path.join(os.environ.get("APPDATA", "."), "AutoClicker", "config.json")
_KEY_NAMES = {0x08: "Backspace", 0x09: "Tab", 0x0D: "Enter", 0x13: "Pause", 0x14: "CapsLock",
              0x20: "Space", 0x21: "PageUp", 0x22: "PageDown", 0x23: "End", 0x24: "Home",
              0x25: "Left", 0x26: "Up", 0x27: "Right", 0x28: "Down", 0x2D: "Insert",
              0x2E: "Delete", 0x91: "ScrollLock"}
# keys that can't be a hotkey: mouse buttons, Esc (cancels), modifiers
_SKIP_KEYS = {1, 2, 4, 5, 6, 0x1B, 0x10, 0x11, 0x12, 0x5B, 0x5C, *range(0xA0, 0xA6)}


def vk_name(vk):
    if 0x70 <= vk <= 0x87:
        return f"F{vk - 0x6F}"
    if 0x60 <= vk <= 0x69:
        return f"Num{vk - 0x60}"
    if 0x30 <= vk <= 0x39 or 0x41 <= vk <= 0x5A:
        return chr(vk)
    return _KEY_NAMES.get(vk, f"Key 0x{vk:02X}")


def cursor_pos():
    p = POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def click(x, y, button, double):
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.03)  # let the move settle so the click lands at the new position
    down, up = EVENTS[button]
    n = 2 if double else 1
    for i in range(n):
        user32.mouse_event(down, 0, 0, 0, 0)
        time.sleep(0.04)  # hold briefly; many apps ignore zero-length presses
        user32.mouse_event(up, 0, 0, 0, 0)
        if i < n - 1:
            time.sleep(0.06)


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
def pick_location(parent, callback):
    """Cover all screens with a faint overlay; the next left click picks the spot."""
    gm = user32.GetSystemMetrics
    vx, vy, vw, vh = gm(76), gm(77), gm(78), gm(79)  # virtual screen
    ov = tk.Toplevel(parent)
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
            callback(*cursor_pos())

    ov.bind("<Button-1>", lambda e: done(True))
    ov.bind("<Escape>", lambda e: done(False))
    ov.focus_force()


class PointForm(ttk.Frame):
    """Editor fields for one point; used for the 'new point' panel and the edit dialog."""

    def __init__(self, parent):
        super().__init__(parent)
        self.v = {k: tk.StringVar() for k in
                  ("x", "y", "min", "sec", "offset", "button", "type")}

        def label(text, r, c):
            ttk.Label(self, text=text).grid(row=r, column=c * 2, sticky="e", padx=(8, 4), pady=3)

        def fields(text, r, c, parts):
            """Row of entries; parts are (prefix, key, suffix)."""
            label(text, r, c)
            fr = ttk.Frame(self)
            fr.grid(row=r, column=c * 2 + 1, sticky="w")
            es = []
            for pre, key, suf in parts:
                if pre:
                    ttk.Label(fr, text=pre).pack(side="left", padx=(0, 2))
                e = ttk.Entry(fr, textvariable=self.v[key], width=6)
                e.pack(side="left")
                if suf:
                    ttk.Label(fr, text=suf).pack(side="left", padx=(2, 0))
                ttk.Frame(fr, width=8).pack(side="left")
                es.append(e)
            return es

        def combo(text, key, vals, r, c):
            label(text, r, c)
            cb = ttk.Combobox(self, textvariable=self.v[key], values=vals, width=9,
                              state="readonly")
            cb.grid(row=r, column=c * 2 + 1, sticky="w")
            return cb

        self.widgets = [
            *fields("Location", 0, 0, [("X", "x", ""), ("Y", "y", "")]),
            *fields("Interval", 1, 0, [("", "min", "min"), ("", "sec", "sec")]),
            combo("Button", "button", list(EVENTS), 0, 1),
            combo("Click type", "type", ["Single", "Double"], 1, 1),
            *fields("± Random offset", 1, 2, [("", "offset", "ms")]),
        ]
        self.pick_btn = ttk.Button(self, text="Pick location", command=self._pick)
        self.pick_btn.grid(row=0, column=4, columnspan=2, padx=12)
        self.widgets.append(self.pick_btn)
        self.set(ClickPoint(x=500, y=500))

    def _pick(self):
        def got(x, y):
            self.v["x"].set(x); self.v["y"].set(y)
        pick_location(self.winfo_toplevel(), got)

    def set(self, p):
        mins, rest = divmod(p.interval_ms, 60000)
        for k, val in (("x", p.x), ("y", p.y), ("min", mins), ("sec", rest // 1000),
                       ("offset", p.offset_ms), ("button", p.button), ("type", p.click_type)):
            self.v[k].set(val)

    def read(self):
        """Build a ClickPoint from the fields; ValueError if invalid."""
        v = self.v
        x, y = int(v["x"].get()), int(v["y"].get())
        mins, secs = (int(v[k].get() or 0) for k in ("min", "sec"))
        offset = int(v["offset"].get() or 0)
        interval = (mins * 60 + secs) * 1000
        if min(mins, secs) < 0 or interval < 1 or offset < 0:
            raise ValueError
        return ClickPoint(x=x, y=y, interval_ms=interval, offset_ms=offset,
                          button=v["button"].get(), click_type=v["type"].get())

    def set_locked(self, locked):
        for w in self.widgets:
            if isinstance(w, ttk.Combobox):
                w.config(state="disabled" if locked else "readonly")
            elif isinstance(w, ttk.Button):
                w.state(["disabled"] if locked else ["!disabled"])
            else:
                w.config(state="disabled" if locked else "normal")


def form_error():
    messagebox.showerror("Invalid value", "X, Y, interval and offset must be whole "
                         "numbers (interval at least 1 sec, offset ≥ 0).")


class EditDialog(tk.Toplevel):
    def __init__(self, parent, point, on_ok):
        super().__init__(parent)
        self.title("Edit point")
        self.transient(parent)
        self.resizable(False, False)
        self.on_ok = on_ok
        self.form = PointForm(self)
        self.form.pack(padx=12, pady=12)
        self.form.set(point)
        bar = ttk.Frame(self)
        bar.pack(fill="x", padx=12, pady=(0, 12))
        ttk.Button(bar, text="Cancel", command=self.destroy).pack(side="right")
        ttk.Button(bar, text="OK", command=self._ok).pack(side="right", padx=4)
        self.bind("<Escape>", lambda e: self.destroy())
        self.withdraw()  # position while hidden to avoid a flash at the top-left
        self.update_idletasks()
        x = parent.winfo_rootx() + (parent.winfo_width() - self.winfo_reqwidth()) // 2
        y = parent.winfo_rooty() + (parent.winfo_height() - self.winfo_reqheight()) // 2
        self.geometry(f"+{max(x, 0)}+{max(y, 0)}")
        self.deiconify()
        self.grab_set()

    def _ok(self):
        try:
            p = self.form.read()
        except ValueError:
            form_error()
            return
        self.on_ok(p)
        self.destroy()


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("AutoClicker")
        self.geometry("760x520")
        self.minsize(700, 480)
        self.points: list[ClickPoint] = []
        self.runner = None
        self.f6_was_down = False

        self._build()
        self.hotkey = VK_F6
        self.capturing = False
        try:
            with open(CONFIG) as fh:
                self.hotkey = int(json.load(fh)["hotkey"])
        except Exception:
            pass
        self._update_hotkey_labels()
        self.after(50, self._poll_hotkey)
        self.protocol("WM_DELETE_WINDOW", self._close)

    # ---- layout
    def _build(self):
        top = ttk.Frame(self, padding=8)
        top.pack(fill="x")
        self.start_btn = ttk.Button(top, command=self.toggle)
        self.start_btn.pack(side="left")
        self.status = ttk.Label(top, text="Stopped")
        self.status.pack(side="left", padx=12)
        self.hotkey_btn = ttk.Button(top, command=self._capture_hotkey)
        self.hotkey_btn.pack(side="left")
        ttk.Button(top, text="Save…", command=self._save).pack(side="right")
        ttk.Button(top, text="Load…", command=self._load).pack(side="right", padx=4)

        cols = ("#", "On", "X", "Y", "Button", "Type", "Interval", "± Offset (ms)")
        self.tree = ttk.Treeview(self, columns=cols, show="headings", height=9,
                                 selectmode="browse")
        for c, w in zip(cols, (35, 40, 70, 70, 80, 80, 110, 110)):
            self.tree.heading(c, text=c)
            self.tree.column(c, width=w, anchor="center")
        self.tree.pack(fill="both", expand=True, padx=8)
        self.tree.bind("<Double-1>", self._toggle_enabled)
        self.tree.bind("<Button-3>", self._context_menu)

        # settings used by "Add point"
        box = ttk.LabelFrame(self, text="New point", padding=8)
        box.pack(fill="x", padx=8, pady=8)
        self.form = PointForm(box)
        self.form.pack(anchor="w")

        bar = ttk.Frame(self, padding=(8, 0, 8, 8))
        bar.pack(fill="x")
        self.list_buttons = [
            ttk.Button(bar, text="Add point", command=self._add_point),
            ttk.Button(bar, text="Remove", command=self._remove),
            ttk.Button(bar, text="Move up", command=lambda: self._move(-1)),
            ttk.Button(bar, text="Move down", command=lambda: self._move(1)),
        ]
        for b in self.list_buttons:
            b.pack(side="left", padx=(0, 4))
        ttk.Label(bar, text="Right-click a point to edit it · double-click to enable/disable"
                  ).pack(side="right")

        self.menu = tk.Menu(self, tearoff=False)
        self.menu.add_command(label="Edit…", command=self._edit)
        self.menu.add_command(label="Enable / disable", command=self._toggle_enabled)
        self.menu.add_command(label="Duplicate", command=self._duplicate)
        self.menu.add_command(label="Remove", command=self._remove)
        self.menu.add_separator()
        self.menu.add_command(label="Move up", command=lambda: self._move(-1))
        self.menu.add_command(label="Move down", command=lambda: self._move(1))

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

    def _context_menu(self, event):
        row = self.tree.identify_row(event.y)
        if self.runner or not row:
            return
        self.tree.selection_set(row)
        self.menu.tk_popup(event.x_root, event.y_root)

    def _add_point(self):
        try:
            p = self.form.read()
        except ValueError:
            form_error()
            return
        self.points.append(p)
        self._refresh(len(self.points) - 1)

    def _edit(self):
        i = self._selected()
        if i is None:
            return

        def save(new):
            new.enabled = self.points[i].enabled
            self.points[i] = new
            self._refresh(i)

        EditDialog(self, self.points[i], save)

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
        self.start_btn.config(text=f"Stop ({vk_name(self.hotkey)})")
        self.status.config(text="Running…")
        self._lock(True)

    def _stopped(self):
        self.runner = None
        self.start_btn.config(text=f"Start ({vk_name(self.hotkey)})")
        self.status.config(text="Stopped")
        self._lock(False)

    def _lock(self, locked):
        self.form.set_locked(locked)
        for b in self.list_buttons:
            b.state(["disabled"] if locked else ["!disabled"])

    def _update_hotkey_labels(self):
        name = vk_name(self.hotkey)
        verb = "Stop" if self.runner else "Start"
        self.start_btn.config(text=f"{verb} ({name})")
        self.hotkey_btn.config(text="Change hotkey…")

    def _capture_hotkey(self):
        self.capturing = True
        self.hotkey_btn.config(text="Press a key… (Esc cancels)")
        self.hotkey_btn.state(["disabled"])

    def _poll_hotkey(self):
        if self.capturing:
            if user32.GetAsyncKeyState(0x1B) & 0x8000:
                self._end_capture()
            else:
                for vk in range(8, 255):
                    if vk not in _SKIP_KEYS and user32.GetAsyncKeyState(vk) & 0x8000:
                        self.hotkey = vk
                        self._end_capture(save=True)
                        break
            self.f6_was_down = True  # swallow the key that was just pressed
        else:
            down = bool(user32.GetAsyncKeyState(self.hotkey) & 0x8000)
            if down and not self.f6_was_down:
                self.toggle()
            self.f6_was_down = down
        self.after(50, self._poll_hotkey)

    def _end_capture(self, save=False):
        self.capturing = False
        self.hotkey_btn.state(["!disabled"])
        self._update_hotkey_labels()
        if save:
            try:
                os.makedirs(os.path.dirname(CONFIG), exist_ok=True)
                with open(CONFIG, "w") as fh:
                    json.dump({"hotkey": self.hotkey}, fh)
            except OSError:
                pass

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

