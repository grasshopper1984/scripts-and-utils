#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <wtsapi32.h>
#include <process.h>
#include <atomic>
#include "gesture.h"
#include "resource.h"

namespace {
using namespace right_middle;
constexpr wchar_t WindowClass[] = L"ScriptsAndUtils.RightMiddle";
constexpr UINT TrayMessage = WM_APP + 1;
constexpr UINT StateMessage = WM_APP + 2;
constexpr UINT SetEnabledMessage = WM_APP + 3;
constexpr UINT StopMessage = WM_APP + 4;
constexpr UINT SessionMessage = WM_APP + 5;
constexpr UINT OutputMessage = WM_APP + 6;
constexpr UINT ToggleId = 1, ExitId = 2, HotkeyId = 1;
constexpr UINT_PTR HoldTimer = 1, CleanupTimer = 2;
constexpr ULONG_PTR InjectionTag = 0x524D4944;
constexpr unsigned HoldMs = 200;
HWND ui = nullptr;
HWND inputWindow = nullptr; // Owned and used only by the input thread.
DWORD inputThreadId = 0;
HANDLE inputThread = nullptr, readyEvent = nullptr;
HHOOK mouseHook = nullptr;
Gesture gesture(HoldMs);
bool middleUpOwed = false, rightUpOwed = false;
bool sessionActive = true, requestedEnabled = true;
bool startPaused = false;
// Owned by the input thread. A hook callback only enqueues replacement input;
// SendInput runs after the callback has returned to Windows.
Result outputQueue[64]{};
unsigned outputHead = 0, outputCount = 0;
bool outputPosted = false, drainingOutput = false, emittingOutput = false;
bool hookFailurePending = false;
std::atomic<bool> enabled{false};
std::atomic<bool> failed{false};
NOTIFYICONDATAW tray{};
UINT taskbarCreated = 0;

DWORD Flag(Output output) noexcept {
    switch (output) {
    case Output::right_down: return MOUSEEVENTF_RIGHTDOWN;
    case Output::right_up: return MOUSEEVENTF_RIGHTUP;
    case Output::middle_down: return MOUSEEVENTF_MIDDLEDOWN;
    case Output::middle_up: return MOUSEEVENTF_MIDDLEUP;
    }
    return 0;
}

// Check SendInput results, including a partially delivered down/up pair.
// Failed releases remain owed and are retried by the input thread's timer.
bool Emit(const Result& result) noexcept {
    if (result.count == 0) return true;
    INPUT inputs[2]{};
    for (unsigned i = 0; i < result.count; ++i) {
        inputs[i].type = INPUT_MOUSE;
        inputs[i].mi.dwFlags = Flag(result.output[i]);
        inputs[i].mi.dwExtraInfo = InjectionTag;
    }
    emittingOutput = true;
    UINT sent = SendInput(result.count, inputs, sizeof(INPUT));
    emittingOutput = false;
    for (UINT i = 0; i < sent; ++i) {
        switch (result.output[i]) {
        case Output::right_down: rightUpOwed = true; break;
        case Output::right_up: rightUpOwed = false; break;
        case Output::middle_down: middleUpOwed = true; break;
        case Output::middle_up: middleUpOwed = false; break;
        }
    }
    return sent == result.count;
}

void UnhookIfPaused() noexcept;

void ReleaseOwed() noexcept {
    Result release;
    if (rightUpOwed) release.Add(Output::right_up);
    if (middleUpOwed && !gesture.PhysicalMiddle()) release.Add(Output::middle_up);
    Emit(release);
    if (rightUpOwed || (middleUpOwed && !gesture.PhysicalMiddle()))
        SetTimer(inputWindow, CleanupTimer, 250, nullptr);
    else KillTimer(inputWindow, CleanupTimer);
    UnhookIfPaused();
}

void UnhookIfPaused() noexcept {
    if (outputCount || drainingOutput || emittingOutput || hookFailurePending) return;
    if (!gesture.Enabled() && gesture.CanUnhook() && mouseHook) {
        // The real button now owns its eventual release; do not retain a stale
        // synthetic release obligation after removing our observer.
        if (gesture.PhysicalMiddle()) middleUpOwed = false;
        // Until failed cleanup succeeds, observe physical takeover so a retry
        // cannot accidentally release a newly pressed physical button.
        if (middleUpOwed || rightUpOwed) return;
        UnhookWindowsHookEx(mouseHook);
        mouseHook = nullptr;
    }
}

void FailOpen() noexcept {
    requestedEnabled = false;
    Emit(gesture.SetEnabled(false));
    KillTimer(inputWindow, HoldTimer);
    ReleaseOwed();
    enabled.store(false);
    failed.store(true);
    PostMessageW(ui, StateMessage, 0, 0);
    UnhookIfPaused();
}

bool Apply(const Result& result) noexcept {
    if (!Emit(result)) { FailOpen(); return false; }
    if (!gesture.Pending()) KillTimer(inputWindow, HoldTimer);
    UnhookIfPaused();
    return true;
}

void DeferHookFailure() noexcept {
    requestedEnabled = false;
    gesture.SetEnabled(false);
    enabled.store(false);
    failed.store(true);
    outputHead = outputCount = 0;
    hookFailurePending = true;
    KillTimer(inputWindow, HoldTimer);
    if (!outputPosted) {
        outputPosted = PostMessageW(inputWindow, OutputMessage, 0, 0) != FALSE;
        if (!outputPosted) SetTimer(inputWindow, CleanupTimer, 1, nullptr);
    }
    PostMessageW(ui, StateMessage, 0, 0);
}

bool QueueOutput(const Result& result) noexcept {
    if (!result.count) return true;
    if (outputCount == ARRAYSIZE(outputQueue)) { DeferHookFailure(); return false; }
    outputQueue[(outputHead + outputCount) % ARRAYSIZE(outputQueue)] = result;
    ++outputCount;
    if (!outputPosted && !drainingOutput) {
        if (!PostMessageW(inputWindow, OutputMessage, 0, 0)) { DeferHookFailure(); return false; }
        outputPosted = true;
    }
    return true;
}

void DrainOutput() noexcept {
    if (drainingOutput) return;
    outputPosted = false;
    drainingOutput = true;
    if (hookFailurePending) {
        hookFailurePending = false;
        FailOpen();
    } else {
        while (outputCount) {
            const Result result = outputQueue[outputHead];
            outputHead = (outputHead + 1) % ARRAYSIZE(outputQueue);
            --outputCount;
            if (!Apply(result)) { outputHead = outputCount = 0; break; }
        }
    }
    drainingOutput = false;
    UnhookIfPaused();
}

LRESULT CALLBACK MouseHook(int code, WPARAM message, LPARAM data) noexcept {
    if (code != HC_ACTION) return CallNextHookEx(nullptr, code, message, data);
    const auto& mouse = *reinterpret_cast<const MSLLHOOKSTRUCT*>(data);
    // All injected events pass through, including our own SendInput output.
    // This also avoids transforming touch/automation events a second time.
    if ((mouse.flags & LLMHF_INJECTED) != 0 || mouse.dwExtraInfo == InjectionTag)
        return CallNextHookEx(nullptr, code, message, data);
    Event event;
    switch (message) {
    case WM_RBUTTONDOWN: event = Event::right_down; break;
    case WM_RBUTTONUP: event = Event::right_up; break;
    case WM_MBUTTONDOWN: event = Event::middle_down; break;
    case WM_MBUTTONUP: event = Event::middle_up; break;
    case WM_MOUSEMOVE:
        if (!gesture.Pending()) return CallNextHookEx(nullptr, code, message, data);
        event = Event::move;
        break;
    default: return CallNextHookEx(nullptr, code, message, data);
    }
    bool wasPending = gesture.Pending();
    Result result = gesture.Input(event, mouse.time);
    // Preserve button order if a physical middle press/release arrives before
    // queued replacement input has been delivered. Otherwise a physical up
    // could pass first and leave a subsequently injected middle-down stuck.
    if ((outputCount || drainingOutput || emittingOutput) && !result.suppress &&
        (event == Event::middle_down || event == Event::middle_up)) {
        result.suppress = true;
        result.Add(event == Event::middle_down ? Output::middle_down : Output::middle_up);
    }
    // Suppressing a real middle-up transfers its release obligation to us,
    // even if the original down was physical rather than SendInput-generated.
    if (event == Event::middle_up) middleUpOwed = result.suppress;
    if ((event == Event::right_down || event == Event::right_up) && !result.suppress)
        rightUpOwed = false;
    if (!wasPending && gesture.Pending()) {
        const DWORD elapsed = GetTickCount() - mouse.time;
        const UINT remaining = elapsed >= HoldMs ? 1 : HoldMs - elapsed;
        if (!SetTimer(inputWindow, HoldTimer, remaining, nullptr)) {
            DeferHookFailure();
            return 1; // Drain this gesture; do not leave a suppressed down paired with a passed up.
        }
    }
    QueueOutput(result);
    if (!gesture.Pending()) KillTimer(inputWindow, HoldTimer);
    UnhookIfPaused();
    return result.suppress ? 1 : CallNextHookEx(nullptr, code, message, data);
}

void SetEnabled(bool value) noexcept {
    DrainOutput();
    requestedEnabled = value;
    value = value && sessionActive;
    if (value && !mouseHook) {
        // A gesture already held when resuming belongs to the original input path.
        // Wait for its release instead of synthesizing a new button press.
        gesture.Synchronize((GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0,
            !middleUpOwed && (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0);
        mouseHook = SetWindowsHookExW(WH_MOUSE_LL, MouseHook, GetModuleHandleW(nullptr), 0);
        if (!mouseHook) { FailOpen(); return; }
    }
    if (!Apply(gesture.SetEnabled(value))) return;
    if (!value) ReleaseOwed();
    enabled.store(value);
    failed.store(false);
    PostMessageW(ui, StateMessage, 0, 0);
}

LRESULT CALLBACK InputProc(HWND window, UINT message, WPARAM w, LPARAM l) {
    switch (message) {
    case OutputMessage:
        DrainOutput();
        return 0;
    case WM_TIMER:
        DrainOutput();
        if (w == HoldTimer) Apply(gesture.Input(Event::deadline, GetTickCount()));
        else if (w == CleanupTimer) ReleaseOwed();
        return 0;
    case SetEnabledMessage:
        SetEnabled(w != 0);
        return 0;
    case SessionMessage: {
        const bool resume = w != 0;
        const bool previous = requestedEnabled;
        sessionActive = resume;
        SetEnabled(resume && previous);
        requestedEnabled = previous;
        if (resume) {
            // Secure desktop/sleep can hide a physical release from this hook.
            gesture.Synchronize((GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0,
                !middleUpOwed && (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0);
            ReleaseOwed();
            UnhookIfPaused();
        }
        return 0;
    }
    case StopMessage:
        DrainOutput();
        Apply(gesture.SetEnabled(false));
        ReleaseOwed();
        if (mouseHook) { UnhookWindowsHookEx(mouseHook); mouseHook = nullptr; }
        DestroyWindow(window);
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, w, l);
}

unsigned __stdcall InputMain(void*) {
    WNDCLASSW wc{};
    wc.lpfnWndProc = InputProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"ScriptsAndUtils.RightMiddle.Input";
    RegisterClassW(&wc);
    inputWindow = CreateWindowExW(0, wc.lpszClassName, L"", 0, 0, 0, 0, 0,
        HWND_MESSAGE, nullptr, wc.hInstance, nullptr);
    if (inputWindow) {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        SetEnabled(!startPaused);
    } else failed.store(true);
    SetEvent(readyEvent);
    if (!inputWindow) return 1;
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) DispatchMessageW(&message);
    return 0;
}

void UpdateTray() {
    // Shared icon resources are cached by Windows; state changes do no drawing or allocation.
    int icon = failed.load() ? IDI_RIGHT_MIDDLE_ERROR : enabled.load() ? IDI_RIGHT_MIDDLE : IDI_RIGHT_MIDDLE_PAUSED;
    tray.hIcon = static_cast<HICON>(LoadImageW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(icon), IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_SHARED));
    const wchar_t* text = failed.load() ? L"Right Middle: paused after input error" :
        enabled.load() ? L"Right Middle: active (hold right 200 ms)" : L"Right Middle: paused";
    lstrcpynW(tray.szTip, text, ARRAYSIZE(tray.szTip));
    Shell_NotifyIconW(NIM_MODIFY, &tray);
}

void Toggle() { PostMessageW(inputWindow, SetEnabledMessage, enabled.load() ? 0 : 1, 0); }

void ShowMenu() {
    HMENU menu = CreatePopupMenu();
    AppendMenuW(menu, MF_STRING, ToggleId, enabled.load() ? L"Pause\tCtrl+Alt+F10" : L"Resume\tCtrl+Alt+F10");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, ExitId, L"Exit");
    POINT point{};
    GetCursorPos(&point);
    SetForegroundWindow(ui);
    UINT command = static_cast<UINT>(TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
        point.x, point.y, 0, ui, nullptr));
    DestroyMenu(menu);
    PostMessageW(ui, WM_NULL, 0, 0);
    if (command == ToggleId) Toggle();
    if (command == ExitId) PostMessageW(ui, WM_CLOSE, 0, 0);
}

LRESULT CALLBACK UiProc(HWND window, UINT message, WPARAM w, LPARAM l) {
    if (taskbarCreated && message == taskbarCreated) { Shell_NotifyIconW(NIM_ADD, &tray); return 0; }
    switch (message) {
    case TrayMessage:
        if (l == WM_RBUTTONUP || l == WM_LBUTTONUP) ShowMenu();
        return 0;
    case WM_HOTKEY:
        if (w == HotkeyId) Toggle();
        return 0;
    case StateMessage:
        UpdateTray();
        return 0;
    case WM_WTSSESSION_CHANGE:
        if (w == WTS_SESSION_LOCK || w == WTS_CONSOLE_DISCONNECT || w == WTS_REMOTE_DISCONNECT)
            PostMessageW(inputWindow, SessionMessage, 0, 0);
        else if (w == WTS_SESSION_UNLOCK || w == WTS_SESSION_LOGON || w == WTS_REMOTE_CONNECT || w == WTS_CONSOLE_CONNECT)
            PostMessageW(inputWindow, SessionMessage, 1, 0);
        return 0;
    case WM_POWERBROADCAST:
        if (w == PBT_APMSUSPEND) PostMessageW(inputWindow, SessionMessage, 0, 0);
        else if (w == PBT_APMRESUMEAUTOMATIC || w == PBT_APMRESUMESUSPEND)
            PostMessageW(inputWindow, SessionMessage, 1, 0);
        return TRUE;
    case WM_QUERYENDSESSION:
        PostMessageW(inputWindow, SetEnabledMessage, 0, 0);
        return TRUE;
    case WM_ENDSESSION:
        if (w) DestroyWindow(window);
        return 0;
    case WM_CLOSE:
        DestroyWindow(window);
        return 0;
    case WM_DESTROY:
        UnregisterHotKey(window, HotkeyId);
        WTSUnRegisterSessionNotification(window);
        Shell_NotifyIconW(NIM_DELETE, &tray);
        PostMessageW(inputWindow, StopMessage, 0, 0);
        // No waiting occurs on the hook thread. Normal process shutdown gives
        // it time to emit the matching button-up and remove its hook.
        if (inputThread) WaitForSingleObject(inputThread, 2000);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, w, l);
}
}

#ifndef RIGHT_MIDDLE_TEST
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR command, int) {
    startPaused = lstrcmpW(command, L"--paused") == 0;
    HANDLE singleton = CreateMutexW(nullptr, FALSE, L"Local\\ScriptsAndUtils.RightMiddle");
    if (!singleton) return 1;
    if (GetLastError() == ERROR_ALREADY_EXISTS) { CloseHandle(singleton); return 0; }
    WNDCLASSW wc{};
    wc.lpfnWndProc = UiProc;
    wc.hInstance = instance;
    wc.lpszClassName = WindowClass;
    wc.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(IDI_RIGHT_MIDDLE));
    RegisterClassW(&wc);
    ui = CreateWindowExW(WS_EX_TOOLWINDOW, WindowClass, L"Right Middle", WS_OVERLAPPED,
        0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    if (!ui) { CloseHandle(singleton); return 1; }
    taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
    tray.cbSize = sizeof(tray);
    tray.hWnd = ui;
    tray.uID = 1;
    tray.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    tray.uCallbackMessage = TrayMessage;
    tray.hIcon = static_cast<HICON>(LoadImageW(instance, MAKEINTRESOURCEW(IDI_RIGHT_MIDDLE), IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_SHARED));
    lstrcpynW(tray.szTip, L"Right Middle: starting", ARRAYSIZE(tray.szTip));
    Shell_NotifyIconW(NIM_ADD, &tray);
    if (!RegisterHotKey(ui, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F10)) {
        DestroyWindow(ui);
        if (lstrcmpW(command, L"--smoke") != 0)
            MessageBoxW(nullptr, L"Ctrl+Alt+F10 is unavailable. Close the app using that shortcut and restart Right Middle. No remapping was started.",
                L"Right Middle", MB_OK | MB_ICONWARNING);
        CloseHandle(singleton);
        return 2;
    }
    WTSRegisterSessionNotification(ui, NOTIFY_FOR_THIS_SESSION);
    readyEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (readyEvent) inputThread = reinterpret_cast<HANDLE>(_beginthreadex(nullptr, 0, InputMain, nullptr, 0,
        reinterpret_cast<unsigned*>(&inputThreadId)));
    if (!readyEvent || !inputThread || WaitForSingleObject(readyEvent, 5000) != WAIT_OBJECT_0) {
        MessageBoxW(nullptr, L"Could not start the input thread. No remapping is active.", L"Right Middle", MB_OK | MB_ICONERROR);
        DestroyWindow(ui);
    }
    // A self-contained smoke run can start and stop the actual hook without clicks.
    if (lstrcmpW(command, L"--smoke") == 0) PostMessageW(ui, WM_CLOSE, 0, 0);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    if (inputThread) CloseHandle(inputThread);
    if (readyEvent) CloseHandle(readyEvent);
    CloseHandle(singleton);
    return failed.load() ? 1 : 0;
}
#endif
