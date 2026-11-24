# Snap

A simple multi-point autoclicker for Windows. It's a native app (C#, WinForms): a single ~155 KB `.exe`, nothing to install.

## Features

Add as many click points as you like. Each point is configured independently:

- **Location** – X/Y screen coordinates, or press **Pick location** and left-click anywhere on screen (Esc cancels)
- **Interval** – time between clicks, set in minutes, seconds and milliseconds (minimum 50 ms)
- **Random offset** – ± milliseconds, re-rolled after every click (next delay = interval + random(−offset, +offset))
- **Mouse button** – left, right or middle
- **Click type** – single or double
- **Label** – an optional name (e.g. "Confirm") shown in the list
- **± Jitter** – each click lands at a random spot within this many pixels of the location (0 = exact)

Other things:

- **Stop automatically after** N clicks or N minutes (0 = no limit, whichever comes first) for unattended runs (remembered between runs)
- Tick **Keep PC awake while running** for long unattended runs: while clicking, the PC won't sleep or turn the display off, and after about 7½ minutes without input (randomised by ±20 seconds, checked once a minute) the mouse is nudged by 1 pixel and back so the screen saver and inactivity lock don't kick in (remembered between runs)
- Tick **Return cursor after each click** to move the mouse back to where it was after every click (remembered between runs)
- Press **F6** to start/stop, even when the window isn't focused; use **Change hotkey…** to pick another key (remembered between runs). The hotkey won't start clicking while a dialog or the location picker is open, and while Snap's own window has focus only F-keys, Pause and Scroll Lock start it, so ordinary keys can't trigger it while you type. Stopping always works
- Your list of points is saved automatically and is there again next time; **Save…** / **Load…** (Ctrl+S / Ctrl+O) export and import it as JSON
- Set up a point in the "New point" panel and click **Add point**; right-click a point to edit, duplicate, remove or reorder it, and double-click it to enable/disable. In the list: **Enter** edits, **Space** enables/disables, **Delete** removes, **Ctrl+D** duplicates and **Alt+↑/↓** reorders. Disabled points are greyed out
- Each point runs on its own timer; the list order only decides which point clicks first when several are due at the same moment
- The window reopens where you left it, at the same size (maximised if it was)
- Snap shows an icon in the system tray instead of a taskbar button. Minimising hides the window; click the tray icon to bring it back, or right-click it to start/stop or exit. The hotkey keeps working while the window is hidden. Starting Snap again just brings the running copy to the front

## Usage

Download `Snap.exe` from the [Releases](../../releases) page and run it. It needs the .NET Framework 4.8, which is built into Windows 10 and 11.

## Building

No SDK or Visual Studio needed, just the C# compiler that ships with Windows:

```
powershell -ExecutionPolicy Bypass -File src\build.ps1
```

The result is `dist\Snap.exe`. The script exits with the compiler's exit code, so a failed build fails CI. Every push and pull request is built by GitHub Actions; pushing a `v*` tag also attaches `Snap.exe` and its SHA-256 checksum to the release.

## Python version

The original Python/tkinter version is kept in [`python/`](python/) (currently 2.3.0; releases up to v1.2.0 were built from it). It is maintained separately and doesn't have every feature of the native app: it applies and preserves point labels and jitter but can't edit them, and has no stop conditions. Setups saved by either version load in the other.

## Notes

- The mouse cursor moves to each point before clicking (and back afterwards if **Return cursor after each click** is ticked).
- To click inside apps running as administrator, run Snap as administrator too. Windows silently drops clicks sent to an elevated window, so Snap can only warn when input is refused outright (for example on the lock screen).
- Settings are kept in `%APPDATA%\Snap\config.json`. Snap was previously called AutoClicker; settings saved under that name are picked up automatically on first run.
- Use responsibly and respect the terms of any software or game you use it with.

## License

GPL-3.0 – see [LICENSE](LICENSE).
