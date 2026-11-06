# Right Middle

A standalone native Windows mouse remapper. **Tap right = right click. Hold right for 200 ms = hold middle; release = release middle.** No AutoHotkey, .NET, packages, services, or drivers. It does not modify Windows settings.

## Use

1. Exit the old AutoHotkey script before enabling this helper. Two remappers on the same button can interfere. Remove the old script or shortcut from Windows Startup when retiring it.
2. Double-click `launch.cmd`, or run `bin/RightMiddle.exe` after building.
3. **Ctrl+Alt+F10** pauses/resumes. Left- or right-click the tray icon for Pause/Resume and Exit. The tray tile is teal while active, slate with pause bars while paused, and red with an exclamation mark after an input error. Windows may hide new tray icons in the overflow area. If that shortcut is unavailable, the helper exits before installing its hook.

A short click is sent immediately on release, without waiting out the 200 ms hold threshold. Normal Windows context menus also open on release. Middle-button drag starts at the threshold. Right-button drag is intentionally replaced by middle-button drag. Existing Ctrl/Shift/Alt state is preserved.

A remapped hold consumes both physical right-button events and emits only middle-button events. While active, unmatched physical right-button releases are also discarded, so a duplicate release after the hold cannot open a stray context menu. A fresh short right-click still works immediately afterward.

Pausing releases the helper's held middle button and consumes the matching right-button release if needed. Once that gesture is finished, the mouse hook is removed entirely. Resuming during an existing physical press passes its release through. `RightMiddle.exe --paused` starts without a mouse hook. Exit uses the same release cleanup. Launching twice keeps one instance.

Startup is optional: create a shortcut to the executable in `shell:startup`. Nothing is installed automatically. To uninstall, exit and remove the executable and any startup shortcut you created. The program creates no settings, log, or history files.

## Build and test

Requires Windows 10/11 x64. Building requires Visual Studio Build Tools with **Desktop development with C++** and a Windows SDK; running requires only the executable and Windows' built-in DLLs. The C/C++ runtime is linked statically.

```powershell
.\build.ps1 -Test
.\bin\RightMiddle.exe
```

If PowerShell blocks the build script:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

`bin/` and `test-output/` are ignored. Exit the helper before rebuilding. `bin/Tests.exe` replays 100,000 gestures, including timestamp rollover, late timers, cancellation, and physical-middle overlap. It also drives the actual hook and delivery code through a fake Windows boundary to check stray right-button releases, deferred event order, injected-input filtering, hook/timer/queue failure, and partial/failed `SendInput` recovery. Every fake `SendInput` call asserts that the hook callback has returned. These tests never send input to the desktop. `bin/RightMiddle.exe --smoke` briefly installs/removes the real hook and exits; it must be run when another instance is not already running.

Validation on 2026-11-01: MSVC warning-free build; 300,449 assertions passed over 100,000 gesture replays and native delivery fault cases. Real hook startup/shutdown, paused startup, graceful exit, and single-instance behavior passed. A manual Chrome link-opening retest was reported working after the deferred-delivery fix. The executable is 111,104 bytes and imports only Windows DLLs. A paused-mode sample of the earlier build used 10.5 MiB resident / 1.7 MiB private memory. These figures are observations, not guarantees; real physical dragging under heavy system load has not been tested.

## Why this is different from the AHK handler

The hook runs on a dedicated thread with a message pump and above-normal **thread** priority. The tray menu runs separately. A hook callback updates a small state machine, places replacement events in a fixed-size queue, and returns. It never calls `SendInput`, sleeps, waits for release, plays a sound, opens a file, acquires an application lock, or asks the UI thread to do work synchronously. The input thread delivers queued events after the callback returns, preserving event order without holding up the original Windows input event. Physical middle-button transitions arriving during delivery join the queue when needed to keep presses and releases balanced. The hold timer exists only while a gesture is pending. Original input timestamps classify releases, and movement can trigger the hold even if timer dispatch is late. Synthetic input is tagged and ignored by the remapper.

The earlier version called `SendInput` from inside the hook. A Chrome link test received an unwanted right-button release and context menu between the remapped middle-down and middle-up, cancelling the new-tab action. Filtering extra releases in the state machine alone did not prevent that path; replacement input is now deferred until after the hook returns.

Button releases are tracked. If Windows rejects an injected event, remapping pauses, releases already-injected downs where possible, and retries any owed release every 250 ms. A physical middle-button press overlapping a remapped hold is respected. Session lock, disconnect, and sleep cancel the current gesture; resume resynchronizes state.

Windows can still delay or silently remove any user-mode low-level hook under severe scheduler stalls. This program is not a real-time driver. Forced termination cannot run cleanup, so use the tray's Exit command. `SendInput` is restricted across privilege levels: run normally for ordinary apps; an elevated target may reject remapped input and cause the helper to pause. The app does not elevate itself. Other remappers, raw-input games, secure desktops, and hardware disconnects require practical testing; synthetic replay is not proof of behavior in every application.

## About context-menu delays

`MenuShowDelay` controls the delay for opening a **submenu on hover**, not a blanket delay before the initial right-click context menu. Menu fade/animation can add visible delay; Explorer also has to assemble its context menu and extension commands. AHK's `Suspend` disables hotkeys but does not terminate a handler that was already running. For a clean comparison, exit AHK and pause this helper, then compare a mouse right-click with **Shift+F10** on the same item. If both are slow, investigate Explorer/menu rendering and extensions rather than the mouse remapper.

The old handler's `KeyWait` keeps a hotkey thread alive until release, and another trigger of that hotkey is normally suppressed while it is running. Its synchronous `SoundBeep` calls also share the script's scheduling. Those are plausible contributors to delayed or missed remapped events; the original load-related desktop failure has not been reproduced and attributed to a single cause.

References: Microsoft's [low-level mouse hook guidance](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc), [SendInput restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput), [menu parameters](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow), and [context-menu message](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-contextmenu); AutoHotkey's [KeyWait](https://www.autohotkey.com/docs/v2/lib/KeyWait.htm), [Suspend](https://www.autohotkey.com/docs/v2/lib/Suspend.htm), and [SoundBeep](https://www.autohotkey.com/docs/v2/lib/SoundBeep.htm) documentation.
