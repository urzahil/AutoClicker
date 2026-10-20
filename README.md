# AutoClicker

A simple multi-point autoclicker for Windows, written in Python with no dependencies (tkinter + ctypes).

## Features

Add as many click points as you like. Each point is configured independently:

- **Location** – X/Y screen coordinates, or press **Pick location** and left-click anywhere on screen (Esc cancels)
- **Interval** – time between clicks, set in minutes and seconds
- **Random offset** – ± milliseconds, re-rolled after every click (next delay = interval + random(−offset, +offset))
- **Mouse button** – left, right or middle
- **Click type** – single or double

Other things:

- Press **F6** to start/stop, even when the window isn't focused; use **Change hotkey…** to pick another key (remembered between runs)
- Set up a point in the "New point" panel and click **Add point**; right-click a point to edit, duplicate, remove or reorder it, and double-click it to enable/disable
- Save and load your setup as JSON

## Usage

Download `AutoClicker.exe` from the Releases page, or run from source:

```
python autoclicker.py
```

Build a standalone executable:

```
pip install pyinstaller
python -m PyInstaller --onefile --windowed --name AutoClicker autoclicker.py
```

The result is in `dist/AutoClicker.exe`.

## Notes

- The mouse cursor moves to each point before clicking.
- To click inside apps running as administrator, run AutoClicker as administrator too.
- Use responsibly and respect the terms of any software or game you use it with.

## License

GPL-3.0 – see [LICENSE](LICENSE).
