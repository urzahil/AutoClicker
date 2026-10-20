# AutoClicker

A simple multi-point autoclicker for Windows. It's a native app (C#, WinForms): a single ~25 KB `.exe`, nothing to install.

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
- Each point runs on its own timer; the list order only decides which point clicks first when several are due at the same moment
- Save and load your setup as JSON

## Usage

Download `AutoClicker.exe` from the [Releases](../../releases) page and run it. It needs the .NET Framework 4.8, which is built into Windows 10 and 11.

## Building

No SDK or Visual Studio needed, just the C# compiler that ships with Windows:

```
powershell -ExecutionPolicy Bypass -File src\build.ps1
```

The result is `dist\AutoClicker.exe`.

## Python version

The original Python/tkinter version (releases up to v1.2.0) is kept in [`python/`](python/). Setups saved by either version load in the other.

## Notes

- The mouse cursor moves to each point before clicking.
- To click inside apps running as administrator, run AutoClicker as administrator too.
- Use responsibly and respect the terms of any software or game you use it with.

## License

GPL-3.0 – see [LICENSE](LICENSE).
