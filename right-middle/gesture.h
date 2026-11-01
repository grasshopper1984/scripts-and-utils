#pragma once
#include <cstdint>

namespace right_middle {
enum class Event { right_down, right_up, middle_down, middle_up, move, deadline };
enum class Output { right_down, right_up, middle_down, middle_up };

struct Result {
    bool suppress = false;
    Output output[2]{};
    unsigned count = 0;
    void Add(Output item) noexcept { output[count++] = item; }
};

// Single-thread-owned, allocation-free state machine. Times are original input
// timestamps, so a delayed message pump cannot turn a quick tap into a hold.
class Gesture {
public:
    enum class Phase { idle, pending, middle, drain, bypass };
    explicit Gesture(std::uint32_t threshold = 200) noexcept : threshold_(threshold) {}

    Result Input(Event event, std::uint32_t time) noexcept {
        Result result;
        if (event == Event::middle_down || event == Event::middle_up) {
            physical_middle_ = event == Event::middle_down;
            result.suppress = phase_ == Phase::middle;
            return result;
        }
        if (event == Event::right_down) {
            if (phase_ == Phase::bypass) return result;
            if (phase_ != Phase::idle) { result.suppress = true; return result; }
            if (!enabled_) return result;
            start_ = time;
            phase_ = Phase::pending;
            result.suppress = true;
            return result;
        }
        if (event == Event::right_up) {
            if (phase_ == Phase::bypass) { phase_ = Phase::idle; return result; }
            if (phase_ == Phase::idle) {
                // An unmatched physical release can open a context menu even
                // though no right-down reached the app. While active, only a
                // known bypassed press may pass its physical release through.
                result.suppress = enabled_;
                return result;
            }
            result.suppress = true;
            if (phase_ == Phase::pending) {
                // If the threshold timer was starved, classify by the release
                // timestamp, then emit a complete click, never a stuck down.
                if (Elapsed(time) < threshold_) {
                    result.Add(Output::right_down);
                    result.Add(Output::right_up);
                } else if (!physical_middle_) {
                    result.Add(Output::middle_down);
                    result.Add(Output::middle_up);
                }
            } else if (phase_ == Phase::middle && !physical_middle_) {
                result.Add(Output::middle_up);
            }
            phase_ = Phase::idle;
            return result;
        }
        if (phase_ == Phase::pending && Elapsed(time) >= threshold_) {
            phase_ = Phase::middle;
            if (!physical_middle_) result.Add(Output::middle_down);
        }
        return result;
    }

    Result SetEnabled(bool enabled) noexcept {
        enabled_ = enabled;
        return enabled ? Result{} : Cancel();
    }

    Result Cancel() noexcept {
        Result result;
        if (phase_ == Phase::middle && !physical_middle_) result.Add(Output::middle_up);
        // Swallow the matching physical right-up after cancelling a swallowed
        // right-down. Passing it through could open an unwanted context menu.
        if (phase_ != Phase::idle && phase_ != Phase::bypass) phase_ = Phase::drain;
        return result;
    }

    void Synchronize(bool rightAlreadyDown, bool middleAlreadyDown) noexcept {
        phase_ = rightAlreadyDown ? Phase::bypass : Phase::idle;
        physical_middle_ = middleAlreadyDown;
    }
    bool Enabled() const noexcept { return enabled_; }
    bool Pending() const noexcept { return phase_ == Phase::pending; }
    bool Idle() const noexcept { return phase_ == Phase::idle; }
    bool CanUnhook() const noexcept { return phase_ == Phase::idle || phase_ == Phase::bypass; }
    bool PhysicalMiddle() const noexcept { return physical_middle_; }
    Phase State() const noexcept { return phase_; }

private:
    std::uint32_t Elapsed(std::uint32_t time) const noexcept { return time - start_; }
    const std::uint32_t threshold_;
    std::uint32_t start_ = 0;
    bool enabled_ = true;
    bool physical_middle_ = false;
    Phase phase_ = Phase::idle;
};
}
