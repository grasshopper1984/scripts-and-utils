# Razer battery and system widget

A small, draggable Windows desktop widget for **Razer Cobra HyperSpeed**, with CPU load/clock, NVIDIA GPU load/clock/temperature, and a Windows firmware temperature reading. The notification-area icon continues to show mouse battery percentage. One portable executable, built with Windows' included .NET Framework compiler. No package downloads, installer, extra driver, service, browser, sensor library, or Synapse dependency.

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

The widget is 264 x 112 logical pixels and scales with the monitor. The top row shows mouse battery and system temperature (**SYS**); the two rows below show **CPU in blue** and **GPU in magenta**, with aligned load and clock columns. A small lightning bolt indicates charging. There are no hover popups or descriptive text lines; reading details remain available to accessibility tools.

Mouse battery polls once per minute, on launch, on resume, and on manual refresh. Hardware readings update every two seconds while the widget is visible; hiding it pauses hardware polling. Each reader permits only one background task at a time. Slow hardware reads do not block the UI or mouse-battery polling. There are no animations or network requests. Configuration contains only position and the pin preference in `%LOCALAPPDATA%\RazerBatteryWidget\settings.txt`. To remove it, exit and delete the executable and that optional settings folder.

## Hardware readings

- **CPU:** busy time across all logical processors, plus estimated average clock while executing instructions. Clock uses Windows' `% Processor Performance` relative to nominal MHz, including turbo above 100%; it is not a per-core instantaneous clock. CPU load measures time busy and can differ from Task Manager's frequency-adjusted utilization.
- **GPU:** the first NVIDIA GPU exposed by the installed driver's `nvml.dll` in Windows System32. Shows its utilization, graphics clock, and GPU temperature. It does not monitor an Intel integrated GPU or launch `nvidia-smi`/PowerShell on each update. NVML utilization may differ from Task Manager's busiest-engine GPU percentage.
- **System temp:** Windows' `Thermal Zone Information(\_TZ.TZ00)\High Precision Temperature`, converted from tenths of Kelvin. This is a machine-specific firmware zone; its mapping to a CPU package or board sensor has **not** been established. It is deliberately not labelled CPU temperature. Firmware controls how often its value actually changes.

These readings work as a normal user on the tested machine. Missing counters, unsupported NVIDIA readings, or absent driver libraries show `--`; failures do not become a fabricated zero or retain an old value as current. A blocked read older than ten seconds also shows `--`. CPU rate counters need two samples after launch or a long pause. Resume resets the sampling baseline and driver connection. Failed NVIDIA initialization is retried at most once every 30 seconds.

This implementation reads the existing Windows and NVIDIA interfaces. It does not access CPU model-specific registers, install a privileged sensor driver, or claim verified CPU die temperature.

## Device support

Specifically supports Cobra HyperSpeed over USB (`1532:00DA`) and its 2.4 GHz receiver (`1532:00DB`). Other Razer products and Bluetooth are not queried. When both supported connections are present, a successful cable reading takes priority. Support is intended for one mouse.

A failed read keeps the last successful percentage in grey. The short tray label identifies it as **last known**, and accessibility tools can read the last successful read time and error. With no previous reading it shows `--`. Charging becomes unknown if that separate query fails. Battery history is kept in memory only. An explicit successful 0% report is treated as empty, not disconnected.

If the mouse sleeps, wake it and choose **Refresh now**. For persistent read failures, reconnect its cable/receiver; compare with Synapse closed if necessary. The app never stops Synapse or changes mouse settings.

## Build and verify

```powershell
.\build.ps1 -Test          # Protocol/state checks and UI rendering at 100%, 150%, 200%
.\build.ps1 -Probe         # Read the actual connected mouse; nonzero exit if unavailable
.\build.ps1 -HardwareProbe # Check all six hardware readings; first CPU sample is unknown
.\bin\BatteryProbe.exe    # Repeat just the live probe without rebuilding
.\bin\HardwareProbe.exe   # Repeat the hardware probe without rebuilding
```

Exit the widget before rebuilding, or use `-OutputDirectory .\bin\preview` to build a separate copy for validation. `bin/` and `test-output/` are ignored by Git. Tests use synthetic data; probes print readings without device paths or serial numbers. A restricted execution sandbox may hide HID devices and Windows performance counters even when an ordinary Windows user can read them.

Tests cover battery protocol/state, sensor units and invalid values, CPU rate warmup/reset, native counter layout, single in-flight reads, recovery after exceptions, nonblocking cleanup, stale readings, and rendering at 100%, 150%, and 200% scaling.

Validation on 2026-11-01: warning-free build, 322 assertions passed, and normal/partial/stale/large-value previews inspected at the supported display scales. The live hardware probe returned all six readings as a normal user; after the initial counter setup, samples took about 6-10 ms. The installed widget was visible and responsive, recovered after hide/show and a simulated resume notification, and kept one instance after a second launch. The wireless mouse initially did not reply, then returned a valid battery reading. Actual suspend/resume, driver removal/reset, unplug/replug, USB/receiver failover, and moving between physical monitors were not tested. Bluetooth is unsupported.

The compact layout was rebuilt with the same 322 passing assertions and inspected at 100%, 150%, and 200% scaling, including large values and unavailable readings. The installed window was responsive at 396 x 168 physical pixels on a 150% display, with live hardware readings updating. Its executable remains 34,816 bytes.

A baseline sample before the compact layout change used a 34,816-byte executable. Over 75.32 seconds with the widget visible and updating, it used 0.516 CPU-seconds: about **0.057% of total CPU capacity** on 12 logical processors. Resident memory was about **66 MiB**, private memory 44 MiB, and GDI/USER object counts stayed constant during that sample. These are observations on one machine, not performance guarantees. Generated executables, previews, and diagnostic logs stay outside Git.

## Implementation

WinForms handles the small UI; `setupapi.dll` and `hid.dll` provide device discovery and feature reports. Handles use zero desired access and shared mode, so the app does not claim mouse input. Only two power-query commands are allowed: class `0x07`, battery `0x80`, charging `0x84`. Replies are checked for status, transaction, command, length, and checksum. Requests use the Cobra transaction ID `0x1F` and wait 35 ms before reading; retries are bounded. Windows controls the duration of synchronous HID calls, which always run off the UI thread.

Hardware uses Windows' [GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes), [PDH counters](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcountervalue), and NVIDIA's existing [NVML device queries](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html). Counter paths are added with the English-counter API for localization support. Driver libraries load only from Windows System32. Hardware cleanup is deferred until the last background read completes, without waiting on the UI thread.

Protocol facts were checked against OpenRazer's [mouse driver](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.c), [device IDs and timings](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.h), [power commands](https://github.com/openrazer/openrazer/blob/master/driver/razerchromacommon.c), and [report checksum](https://github.com/openrazer/openrazer/blob/master/driver/razercommon.c). This is an independent C# implementation; it does not bundle OpenRazer code or libraries.
