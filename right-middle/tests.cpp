#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <vector>
#include <initializer_list>
#include <limits>

// Exercise the actual hook/delivery code with a fake OS boundary. These tests
// never inject into the desktop, claim a hook, move the pointer, or change settings.
UINT WINAPI FakeSendInput(UINT, LPINPUT, int);
UINT_PTR WINAPI FakeSetTimer(HWND, UINT_PTR, UINT, TIMERPROC);
BOOL WINAPI FakeKillTimer(HWND, UINT_PTR);
BOOL WINAPI FakePostMessage(HWND, UINT, WPARAM, LPARAM);
LRESULT WINAPI FakeNextHook(HHOOK, int, WPARAM, LPARAM);
BOOL WINAPI FakeUnhook(HHOOK);
HHOOK WINAPI FakeInstallHook(int, HOOKPROC, HINSTANCE, DWORD);
SHORT WINAPI FakeAsyncKey(int);
#define RIGHT_MIDDLE_TEST
#define SendInput FakeSendInput
#define SetTimer FakeSetTimer
#define KillTimer FakeKillTimer
#define PostMessageW FakePostMessage
#define CallNextHookEx FakeNextHook
#define UnhookWindowsHookEx FakeUnhook
#define SetWindowsHookExW FakeInstallHook
#define GetAsyncKeyState FakeAsyncKey
#include "main.cpp"

namespace {
unsigned checks = 0;
std::vector<DWORD> delivered;
std::vector<UINT> sendPlan;
unsigned planIndex = 0;
bool holdTimerOn = false, cleanupTimerOn = false, failTimer = false, failHook = false;
bool failOutputPost = false;
bool physicalRight = false, physicalMiddle = false;
bool insideHook = false;
unsigned nextCalls = 0;

void Check(bool condition, const char* description) {
    ++checks;
    if (!condition) { std::fprintf(stderr, "FAIL: %s\n", description); std::exit(1); }
}

void Expect(const Result& result, bool suppress, std::initializer_list<Output> outputs) {
    Check(result.suppress == suppress, "suppression matches gesture ownership");
    Check(result.count == outputs.size(), "exact output count");
    unsigned i = 0;
    for (auto output : outputs) Check(result.output[i++] == output, "ordered output");
}

void ResetRuntime() {
    outputHead = outputCount = 0;
    outputPosted = drainingOutput = emittingOutput = hookFailurePending = false;
    gesture.Synchronize(false, false);
    gesture.SetEnabled(true);
    mouseHook = reinterpret_cast<HHOOK>(1);
    middleUpOwed = rightUpOwed = false;
    requestedEnabled = sessionActive = true;
    enabled.store(true);
    failed.store(false);
    delivered.clear(); sendPlan.clear(); planIndex = 0;
    holdTimerOn = cleanupTimerOn = failTimer = failHook = false;
    failOutputPost = false;
    physicalRight = physicalMiddle = false;
}

LRESULT Mouse(UINT message, DWORD time, DWORD flags = 0, ULONG_PTR tag = 0, bool drain = true) {
    MSLLHOOKSTRUCT data{};
    data.time = time; data.flags = flags; data.dwExtraInfo = tag;
    insideHook = true;
    const auto result = MouseHook(HC_ACTION, message, reinterpret_cast<LPARAM>(&data));
    insideHook = false;
    if (drain) DrainOutput();
    return result;
}

void GestureTests() {
    for (std::uint32_t start : { 0u, 1000u, 0xFFFFFF80u }) {
        for (std::uint32_t duration : { 0u, 1u, 80u, 199u, 200u, 201u, 10000u }) {
            Gesture g;
            Expect(g.Input(Event::right_down, start), true, {});
            if (duration < 200)
                Expect(g.Input(Event::right_up, start + duration), true, {Output::right_down, Output::right_up});
            else
                Expect(g.Input(Event::right_up, start + duration), true, {Output::middle_down, Output::middle_up});
            Expect(g.Input(Event::deadline, start + 20000), false, {});
            Check(g.Idle(), "late timer cannot resurrect a released gesture");
        }
    }
    Gesture g;
    Expect(g.Input(Event::right_down, 0), true, {});
    Expect(g.Input(Event::right_down, 40), true, {}); // Duplicate down does not restart threshold.
    Expect(g.Input(Event::move, 199), false, {});
    Expect(g.Input(Event::move, 200), false, {Output::middle_down});
    Expect(g.Input(Event::deadline, 500), false, {});
    Expect(g.Input(Event::right_up, 600), true, {Output::middle_up});
    Expect(g.Input(Event::right_up, 601), true, {});
    Expect(g.Input(Event::right_up, 800), true, {});

    Expect(g.Input(Event::right_down, 1000), true, {});
    Expect(g.SetEnabled(false), false, {});
    Expect(g.Input(Event::deadline, 1400), false, {});
    Expect(g.Input(Event::right_up, 1500), true, {});
    Expect(g.Input(Event::right_down, 1600), false, {});
    Expect(g.Input(Event::right_up, 1601), false, {});
    g.SetEnabled(true);
    g.Input(Event::right_down, 2000);
    Expect(g.Input(Event::deadline, 2200), false, {Output::middle_down});
    Expect(g.SetEnabled(false), false, {Output::middle_up});
    g.SetEnabled(true); // Resuming during the same held gesture must not start another drag.
    Expect(g.Input(Event::move, 2500), false, {});
    Expect(g.Input(Event::right_up, 2600), true, {});

    // Real middle-button ownership must survive overlap with the remapped hold.
    Expect(g.Input(Event::middle_down, 3000), false, {});
    g.Input(Event::right_down, 3010);
    Expect(g.Input(Event::deadline, 3210), false, {});
    Expect(g.Input(Event::middle_up, 3300), true, {});
    Expect(g.Input(Event::right_up, 3400), true, {Output::middle_up});
    g.Input(Event::right_down, 4000);
    Expect(g.Input(Event::deadline, 4200), false, {Output::middle_down});
    Expect(g.Input(Event::middle_down, 4300), true, {});
    Expect(g.Input(Event::right_up, 4400), true, {});
    Expect(g.Input(Event::middle_up, 4500), false, {});
    g.Synchronize(true, false);
    Expect(g.Input(Event::right_up, 5000), false, {});
    Check(g.Idle(), "starting during a physical press passes its release");
    Expect(g.Input(Event::right_up, 5001), true, {});

    // Active remapping owns physical right releases, including orphan releases
    // after a hold or a completed tap. Only a known pre-existing press bypasses.
    Gesture orphan;
    Expect(orphan.Input(Event::right_up, 0), true, {});
    Expect(orphan.Input(Event::right_down, 10), true, {});
    Expect(orphan.Input(Event::right_up, 50), true, {Output::right_down, Output::right_up});
    Expect(orphan.Input(Event::right_up, 51), true, {});
    orphan.SetEnabled(false);
    Expect(orphan.Input(Event::right_up, 52), false, {});

    // Repeat alternating short clicks, timed drags, and cancellation. Every
    // injected button-down must be matched exactly once, even with late timers.
    int rightBalance = 0, middleBalance = 0;
    const auto consume = [&](Result result) {
        for (unsigned i = 0; i < result.count; ++i) {
            switch (result.output[i]) {
            case Output::right_down: ++rightBalance; break;
            case Output::right_up: --rightBalance; break;
            case Output::middle_down: ++middleBalance; break;
            case Output::middle_up: --middleBalance; break;
            }
            Check(rightBalance >= 0 && rightBalance <= 1 && middleBalance >= 0 && middleBalance <= 1,
                "replay has no unmatched up or duplicate down");
        }
    };
    for (unsigned i = 0; i < 100000; ++i) {
        g.SetEnabled(true);
        const std::uint32_t start = i * 2000;
        consume(g.Input(Event::right_down, start));
        if (i % 3 == 0) consume(g.Input(Event::right_up, start + 50));
        else {
            consume(g.Input(Event::deadline, start + 215));
            if (i % 3 == 1) consume(g.SetEnabled(false));
            consume(g.Input(Event::right_up, start + 600));
        }
        consume(g.Input(Event::deadline, start + 1000));
        Check(g.Idle() && rightBalance == 0 && middleBalance == 0, "every replay gesture finishes released");
    }
}

void RuntimeTests() {
    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0, 0, 0, false);
    Mouse(WM_MOUSEMOVE, 210, 0, 0, false);
    Mouse(WM_RBUTTONUP, 400, 0, 0, false);
    Check(delivered.empty() && outputCount == 2, "hook queues middle events without calling SendInput");
    DrainOutput();
    Check(delivered == std::vector<DWORD>({MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP}),
        "deferred hold delivery preserves button order");
    Check(!middleUpOwed && !outputCount, "deferred hold finishes released");

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0, 0, 0, false);
    Mouse(WM_MOUSEMOVE, 210, 0, 0, false);
    Mouse(WM_RBUTTONUP, 400, 0, 0, false);
    Check(Mouse(WM_MBUTTONDOWN, 410, 0, 0, false) == 1, "physical middle-down waits behind queued releases");
    Check(Mouse(WM_MBUTTONUP, 420, 0, 0, false) == 1, "physical middle-up keeps its place in the output queue");
    DrainOutput();
    Check(delivered == std::vector<DWORD>({MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP,
        MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP}), "physical middle click cannot overtake remapped input");
    Check(!middleUpOwed, "queued physical click is balanced");

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0, 0, 0, false);
    Mouse(WM_MOUSEMOVE, 210, 0, 0, false);
    Mouse(WM_MBUTTONDOWN, 220, 0, 0, false);
    Mouse(WM_RBUTTONUP, 300, 0, 0, false);
    Mouse(WM_MBUTTONUP, 310, 0, 0, false);
    DrainOutput();
    Check(delivered == std::vector<DWORD>({MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP}),
        "overlapping real middle release cannot precede a queued remapped down");
    Check(!middleUpOwed, "overlapping queued gesture finishes released");

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0, 0, 0, false);
    Mouse(WM_MOUSEMOVE, 210, 0, 0, false);
    SetEnabled(false);
    Check(delivered == std::vector<DWORD>({MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP}),
        "pause drains queued input before releasing the held button");
    Check(Mouse(WM_RBUTTONUP, 400) == 1 && !mouseHook, "pause still consumes the owned physical release");

    ResetRuntime();
    failOutputPost = true;
    Mouse(WM_RBUTTONDOWN, 0, 0, 0, false);
    Mouse(WM_MOUSEMOVE, 210, 0, 0, false);
    Check(!enabled.load() && hookFailurePending && cleanupTimerOn, "queue wake failure schedules deferred cleanup");
    InputProc(nullptr, WM_TIMER, CleanupTimer, 0);
    Check(delivered.empty() && !middleUpOwed, "failed queue never leaves an injected button down");
    Check(Mouse(WM_RBUTTONUP, 400) == 1 && !mouseHook, "queue failure drains right-up before unhooking");

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0);
    Mouse(WM_MOUSEMOVE, 210);
    Mouse(WM_RBUTTONUP, 400, 0, 0, false);
    for (unsigned i = 0; i < ARRAYSIZE(outputQueue); ++i) {
        Mouse(WM_RBUTTONDOWN, 500 + i * 100, 0, 0, false);
        Mouse(WM_RBUTTONUP, 550 + i * 100, 0, 0, false);
    }
    Check(hookFailurePending && !enabled.load() && !outputCount,
        "bounded queue overflow drops undelivered input and pauses without blocking");
    DrainOutput();
    Check(!middleUpOwed && !mouseHook && delivered == std::vector<DWORD>({MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP}),
        "queue overflow still releases a previously delivered middle-down");

    ResetRuntime();
    Check(Mouse(WM_RBUTTONDOWN, 10) == 1 && holdTimerOn, "physical down arms a timer without waiting");
    Check(Mouse(WM_RBUTTONUP, 60) == 1 && !holdTimerOn, "release cancels hold timer immediately");
    Check(delivered == std::vector<DWORD>({MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP}), "native short click delivery");
    Check(!rightUpOwed && !middleUpOwed, "no outstanding button after tap");
    for (UINT message : {WM_MOUSEMOVE, WM_RBUTTONDOWN, WM_RBUTTONUP, WM_MBUTTONDOWN, WM_MBUTTONUP})
        Check(Mouse(message, 70, LLMHF_INJECTED) == 77, "injected input passes to next hook unchanged");
    Check(Mouse(WM_RBUTTONDOWN, 80, 0, InjectionTag) == 77, "own output cannot recursively remap");
    Check(MouseHook(-1, 0, 0) == 77, "negative hook codes pass through without dereferencing data");

    // A long hold, its physical release, and duplicate/orphan right releases
    // must deliver exactly one middle down/up pair and no right-button event.
    for (bool lateTimer : {false, true}) {
        ResetRuntime();
        const auto before = nextCalls;
        Check(Mouse(WM_RBUTTONDOWN, 0) == 1, "hold consumes physical right-down");
        if (!lateTimer) Mouse(WM_MOUSEMOVE, 200);
        Check(Mouse(WM_RBUTTONUP, 400) == 1, "hold consumes physical right-up");
        const auto afterRelease = nextCalls;
        Check(afterRelease == before + (lateTimer ? 0u : 1u), "only movement passes through a remapped hold");
        Check(Mouse(WM_RBUTTONUP, 401) == 1, "duplicate right-up after hold is swallowed");
        Check(Mouse(WM_RBUTTONUP, 1000) == 1, "late orphan right-up after hold is swallowed");
        Check(nextCalls == afterRelease, "no right release reaches the next hook after a hold");
        Check(delivered == std::vector<DWORD>({MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP}),
            "hold emits only one middle down/up pair");
        Check(!rightUpOwed && !middleUpOwed, "hold leaves no outstanding button");
        Mouse(WM_RBUTTONDOWN, 1100);
        Mouse(WM_RBUTTONUP, 1150);
        Check(delivered == std::vector<DWORD>({MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP,
            MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP}), "the next short right-click still works");
        Check(Mouse(WM_RBUTTONUP, 1151) == 1, "duplicate right-up after tap is swallowed");
    }

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0);
    Mouse(WM_MOUSEMOVE, 250);
    Check(middleUpOwed && !holdTimerOn, "movement can trigger hold when timer is late");
    SetEnabled(false);
    Check(!middleUpOwed && mouseHook != nullptr && !enabled.load(), "pause releases synthetic middle, then drains right-up");
    Check(Mouse(WM_RBUTTONUP, 300) == 1 && mouseHook == nullptr, "pause unhooks after consuming owned release");
    SetEnabled(true);
    Check(mouseHook != nullptr && enabled.load(), "resume reinstalls hook");

    ResetRuntime();
    sendPlan = {1, 0, 1}; // Half a right click, failed cleanup, successful retry.
    Mouse(WM_RBUTTONDOWN, 0);
    Mouse(WM_RBUTTONUP, 50);
    Check(failed.load() && !enabled.load() && rightUpOwed && cleanupTimerOn, "partial SendInput fails open and retains owed up");
    ReleaseOwed();
    Check(!rightUpOwed && !cleanupTimerOn && mouseHook == nullptr, "cleanup retries the missing up only, then unhooks");
    Check(delivered == std::vector<DWORD>({MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP}), "partial click is eventually balanced");

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0);
    Mouse(WM_MOUSEMOVE, 210);
    sendPlan = {0, 0, 1};
    Mouse(WM_RBUTTONUP, 400);
    Check(middleUpOwed && cleanupTimerOn && !enabled.load(), "blocked middle-up remains owed");
    ReleaseOwed();
    Check(!middleUpOwed && !cleanupTimerOn, "blocked release recovers");

    ResetRuntime();
    Mouse(WM_MBUTTONDOWN, 0);
    Mouse(WM_RBUTTONDOWN, 10);
    Mouse(WM_MOUSEMOVE, 220);
    Check(delivered.empty(), "overlap does not duplicate a real middle-down");
    Check(Mouse(WM_MBUTTONUP, 230) == 1 && middleUpOwed, "suppressed real middle-up transfers release ownership");
    sendPlan = {0, 0, 1};
    Mouse(WM_RBUTTONUP, 300);
    Check(cleanupTimerOn && middleUpOwed, "failed release of transferred ownership is retried");
    ReleaseOwed();
    Check(!middleUpOwed && delivered == std::vector<DWORD>({MOUSEEVENTF_MIDDLEUP}), "transferred real middle-down is released");

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0);
    Mouse(WM_MOUSEMOVE, 210);
    Mouse(WM_MBUTTONDOWN, 220);
    Mouse(WM_RBUTTONUP, 230);
    SetEnabled(false);
    Check(!middleUpOwed && mouseHook == nullptr, "unhook transfers outstanding middle-up back to the held physical button");

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0);
    Mouse(WM_MOUSEMOVE, 210);
    sendPlan = {0, 0};
    InputProc(nullptr, SessionMessage, 0, 0);
    Check(middleUpOwed, "session lock preserves a blocked release for retry");
    physicalMiddle = true; // Async state can still reflect our own injected down.
    InputProc(nullptr, SessionMessage, 1, 0);
    Check(!middleUpOwed, "unlock does not mistake our synthetic down for a real held middle button");

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0);
    Mouse(WM_MOUSEMOVE, 210);
    sendPlan = {0, 0};
    Mouse(WM_RBUTTONUP, 300);
    Check(mouseHook != nullptr && middleUpOwed, "observe real input while a failed release is outstanding");
    Mouse(WM_MBUTTONDOWN, 350);
    Check(mouseHook == nullptr && !middleUpOwed, "a fresh physical middle press takes over its own release");
    const auto sentBeforeRetry = delivered.size();
    ReleaseOwed();
    Check(delivered.size() == sentBeforeRetry, "cleanup must not release the new physical middle press");

    ResetRuntime();
    sendPlan = {1, 0};
    Mouse(WM_RBUTTONDOWN, 0);
    Mouse(WM_RBUTTONUP, 50);
    Mouse(WM_RBUTTONDOWN, 100);
    Check(!rightUpOwed && mouseHook == nullptr, "fresh physical right press takes over its own release");

    ResetRuntime();
    Mouse(WM_RBUTTONDOWN, 0);
    InputProc(nullptr, SessionMessage, 0, 0);
    SetEnabled(false);
    InputProc(nullptr, SessionMessage, 1, 0);
    Check(!enabled.load() && mouseHook == nullptr, "paused state survives sleep and resynchronization removes the drain hook");

    ResetRuntime();
    failTimer = true;
    Check(Mouse(WM_RBUTTONDOWN, 0) == 1 && !enabled.load(), "timer failure disables remapping");
    Check(Mouse(WM_RBUTTONUP, 50) == 1 && mouseHook == nullptr, "timer failure consumes matching up and unhooks");
    ResetRuntime();
    SetEnabled(false);
    failHook = true;
    SetEnabled(true);
    Check(failed.load() && !enabled.load() && mouseHook == nullptr, "hook install failure stays disabled");
}
}

UINT WINAPI FakeSendInput(UINT count, LPINPUT inputs, int size) {
    Check(!insideHook, "SendInput must never run inside the mouse hook");
    Check(size == sizeof(INPUT), "native INPUT structure size");
    UINT sent = planIndex < sendPlan.size() ? sendPlan[planIndex++] : count;
    if (sent > count) sent = count;
    for (UINT i = 0; i < sent; ++i) {
        Check(inputs[i].type == INPUT_MOUSE && inputs[i].mi.dwExtraInfo == InjectionTag, "tagged mouse output");
        delivered.push_back(inputs[i].mi.dwFlags);
    }
    return sent;
}
UINT_PTR WINAPI FakeSetTimer(HWND, UINT_PTR id, UINT, TIMERPROC) {
    if (failTimer) return 0;
    if (id == HoldTimer) holdTimerOn = true;
    if (id == CleanupTimer) cleanupTimerOn = true;
    return id;
}
BOOL WINAPI FakeKillTimer(HWND, UINT_PTR id) {
    if (id == HoldTimer) holdTimerOn = false;
    if (id == CleanupTimer) cleanupTimerOn = false;
    return TRUE;
}
BOOL WINAPI FakePostMessage(HWND, UINT message, WPARAM, LPARAM) {
    return message == OutputMessage && failOutputPost ? FALSE : TRUE;
}
LRESULT WINAPI FakeNextHook(HHOOK, int, WPARAM, LPARAM) { ++nextCalls; return 77; }
BOOL WINAPI FakeUnhook(HHOOK) { return TRUE; }
HHOOK WINAPI FakeInstallHook(int, HOOKPROC, HINSTANCE, DWORD) { return failHook ? nullptr : reinterpret_cast<HHOOK>(1); }
SHORT WINAPI FakeAsyncKey(int key) { return (key == VK_RBUTTON ? physicalRight : physicalMiddle) ? static_cast<SHORT>(0x8000) : 0; }

int main() {
    // Keep the complete native translation unit checked at warning level 4.
    (void)&InputMain; (void)&UiProc;
    GestureTests();
    RuntimeTests();
    std::printf("PASS: %u assertions; 100000 gesture replays; native delivery fault tests.\n", checks);
    return 0;
}
