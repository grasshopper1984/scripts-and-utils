# Razer battery widget

A small, draggable Windows desktop widget for **Razer Cobra HyperSpeed**. Shows battery percentage and charging status, with a percentage icon in the notification area. One portable executable, built with Windows' included .NET Framework compiler. No package downloads, installer, service, browser, or Synapse dependency.

## Run

Double-click **launch.cmd**, or run **bin/RazerBattery.exe** after building. Windows 10 version 1607 or later / Windows 11 with .NET Framework 4.7.2 or later is required. Run as a normal user.

```powershell
.\build.ps1
.\bin\RazerBattery.exe
```

Only `RazerBattery.exe` is needed to run; it can be copied elsewhere. `launch.cmd` builds it on first use. If PowerShell blocks the build script, use this one-process override:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

- Drag the widget to move it. Position and the **Always on top** preference are remembered.
- Right-click the widget or tray icon to refresh, toggle always on top, hide/show, or exit.
- Left-click the tray icon, or launch the executable again, to show a hidden widget.
- **Ctrl+R** refreshes; **Esc** hides. Windows may initially put the tray icon in the overflow menu.
- Optional startup: put a shortcut to the executable in `shell:startup` using File Explorer. Startup is not installed automatically.

The widget is 204 x 80 logical pixels and scales with the monitor. It polls once per minute, on launch, on resume, and on manual refresh. Reads run on one background task at a time. There are no animations or network requests. Configuration contains only position and the pin preference in `%LOCALAPPDATA%\RazerBatteryWidget\settings.txt`. To remove it, exit and delete the executable and that optional settings folder.

## Device support

Specifically supports Cobra HyperSpeed over USB (`1532:00DA`) and its 2.4 GHz receiver (`1532:00DB`). Other Razer products and Bluetooth are not queried. When both supported connections are present, a successful cable reading takes priority. Support is intended for one mouse.

A failed read keeps the last successful percentage in grey and labels it **Last known**. Hover for the last read time and error. With no previous reading it shows `--`. Charging becomes unknown if that separate query fails. Battery history is kept in memory only. An explicit successful 0% report is treated as empty, not disconnected.

If the mouse sleeps, wake it and choose **Refresh now**. For persistent read failures, reconnect its cable/receiver; compare with Synapse closed if necessary. The app never stops Synapse or changes mouse settings.

## Build and verify

```powershell
.\build.ps1 -Test          # Protocol/state checks and UI rendering at 100%, 150%, 200%
.\build.ps1 -Probe         # Read the actual connected mouse; nonzero exit if unavailable
.\bin\BatteryProbe.exe    # Repeat just the live probe without rebuilding
```

Exit the widget before rebuilding. `bin/` and `test-output/` are ignored by Git. Tests use synthetic data; the probe prints only the model, product ID, report length, percentage, charging, and connection, without device paths or serial numbers. A restricted execution sandbox may hide HID devices even when an ordinary Windows user can read them.

Validation on 2026-11-01: warning-free build; 297 assertions passed; rendered normal, low, stale, full, and disconnected states were inspected; a real Cobra HyperSpeed returned battery and charging over USB. The running widget's window was confirmed visible, and a second launch exited while preserving one instance. The executable was 24,064 bytes. One local 75-second idle sample used about 42 MiB of resident memory (24 MiB private) and 0.016 CPU-seconds; these are observations, not performance guarantees. Physical receiver/Bluetooth behavior, unplug/replug, and moving between actual monitors were not tested. Bluetooth is unsupported. Source, filenames, and documentation passed a credential-pattern scan and manual public-data review; generated files stay ignored.

## Implementation

WinForms handles the small UI; `setupapi.dll` and `hid.dll` provide device discovery and feature reports. Handles use zero desired access and shared mode, so the app does not claim mouse input. Only two power-query commands are allowed: class `0x07`, battery `0x80`, charging `0x84`. Replies are checked for status, transaction, command, length, and checksum. Requests use the Cobra transaction ID `0x1F` and wait 35 ms before reading; retries are bounded. Windows controls the duration of synchronous HID calls, which always run off the UI thread.

Protocol facts were checked against OpenRazer's [mouse driver](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.c), [device IDs and timings](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.h), [power commands](https://github.com/openrazer/openrazer/blob/master/driver/razerchromacommon.c), and [report checksum](https://github.com/openrazer/openrazer/blob/master/driver/razercommon.c). This is an independent C# implementation; it does not bundle OpenRazer code or libraries.
