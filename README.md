# WinStats

A tiny system monitor for the Windows 11 taskbar, inspired by
[Stats for macOS](https://github.com/exelban/stats). Each module gets its own
tray icon that shows a live value; a click opens the details.

![WinStats icons in the Windows 11 taskbar](screenshot.png)

*The tray icons in the "Bar with symbol" style (enlarged 2×): display input,
battery, network, disk, memory, GPU and CPU.*

It is a single C# file with no dependencies beyond what ships with Windows.

> **Created solely by AI.** All code and documentation in this repository were
> written by an AI (Claude, via Claude Code) from short prompts; no line was
> written by hand. It has been tried on one machine only, so treat it
> accordingly.

## Modules

| Module  | Tray icon shows              | Click opens                                                        |
|---------|------------------------------|--------------------------------------------------------------------|
| CPU     | Total load in %              | History, per-core bars, thermal zone, uptime, top processes        |
| GPU     | Load of the busiest adapter  | History, load and dedicated memory per adapter, installed GPUs     |
| Memory  | Used RAM in %                | History, used / available / committed, top processes               |
| Disk    | Used space on the system drive | Read/write speed and activity history, free space of all drives  |
| Network | Upload (red) and download (blue) speed | History, interface, local IP, totals since start         |
| Battery | Charge in %                  | History, power source, state, time remaining                       |
| Display | Active monitor input (e.g. `DP1`) | A menu to switch the monitor's input source                   |

The battery module only appears on machines with a battery.

## Usage

Start `WinStats.exe`. Windows 11 usually puts new tray icons into the overflow
flyout (the `^` arrow): drag them onto the taskbar, or enable them under
*Settings → Personalization → Taskbar → Other system tray icons*.

- **Left-click** an icon: details popup (Display: input menu). Click elsewhere or press `Esc` to close.
- **Right-click** any icon:
  - **Modules** – switch individual icons on or off
  - **Widget** – how the icons are drawn (see below)
  - **Update interval** – 1, 2 or 5 seconds
  - **Start with Windows**
  - **Task Manager**, **Quit WinStats**

### Widget styles

| Style             | Icon                                         |
|-------------------|----------------------------------------------|
| Mini              | Three-letter label above the value           |
| Bar with letter   | First letter of the module next to a level bar |
| Line chart        | Recent history as a small chart              |
| Bar with symbol   | Module symbol next to a level bar            |
| Symbol with value | Module symbol above the value                |

The network icon always shows its two speeds, except in the line chart style.

### Switching monitor inputs

The Display module talks to external monitors over DDC/CI (VCP feature `0x60`),
the same channel vendor tools use. It lists every monitor that answers, with the
inputs the monitor reports, and switches when you pick one.

- DDC/CI must be enabled in the monitor's on-screen menu. Laptop panels and some
  docks or adapters do not support it and are not listed.
- After switching away from the input this PC is connected to, the menu is no
  longer visible. Switch back with the monitor's buttons or from the other device.
- Input names follow the VESA MCCS standard. Monitors that use their own codes
  (often for USB-C) show up as `Input 0x..`.

## Building

No SDK is needed; `build.cmd` uses the C# compiler that is part of the
.NET Framework 4.x included in Windows:

```
build.cmd
```

This produces `WinStats.exe` next to the source. Quit a running WinStats first,
otherwise the file is locked. The compiler is C# 5, so the source avoids newer
language features.

## How it works

- Readings come from Windows performance counters (CPU, GPU, disk), Win32 calls
  (memory, battery), `NetworkInterface` statistics (network) and `dxva2.dll`
  (monitor control). Nothing is sent anywhere.
- Icons are drawn as vectors at the exact pixel size the taskbar asks for at its
  current DPI (16 px at 100 %, 24 px at 150 %, 32 px at 200 %) and follow the
  light or dark taskbar theme.
- Settings are stored under `HKCU\Software\WinStats`; "Start with Windows" adds
  an entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- Two diagnostic switches render without starting the tray app:
  `WinStats.exe --preview out.png` draws all widgets and popups into an image,
  `WinStats.exe --monitors out.txt` lists the detected monitors and inputs.

## Limitations

- No fan or per-component temperature sensors: on Windows these need a kernel
  driver. The CPU popup shows the ACPI thermal zone temperature where available.
- GPU adapters are numbered rather than named in the popup.
- Per-process CPU time of some protected system processes cannot be read
  without administrator rights; those are left out of the top-processes list.
