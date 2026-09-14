#include "shell_state.hpp"
#include <iostream>
#include <limits>

int main() {
    int checks = 0;
    auto check = [&](bool condition, const char* name) {
        ++checks;
        if (!condition) { std::cerr << "FAIL: " << name << '\n'; throw std::runtime_error(name); }
    };
    try {
        core::ShellState state;
        check(state.visible && !state.locked, "initial unlocked visibility");
        state.toggleLocked(); state.toggleVisible(); state.toggleVisible();
        check(state.visible && state.locked, "hide/show preserves lock");
        check(state.shouldShow(false, false), "always mode preserves manual show");
        check(!state.shouldShow(true, false), "foreground mode hides outside target game");
        check(state.shouldShow(true, true), "foreground mode shows over target game");
        state.toggleVisible();
        check(!state.shouldShow(true, true), "manual hide is not overridden by foreground");
        state.toggleVisible();
        check(core::hitTest(100, 100, 640, 420, state) == core::Hit::through, "locked input passes through");
        state.toggleLocked(); state.backgroundOpacity = 0;
        for (unsigned dpi : {96U, 120U, 144U, 192U, 240U}) {
            check(core::toPixels(640, dpi) == static_cast<int>(640 * dpi / 96), "DIP physical size");
            check(std::abs(core::toDip(core::toPixels(8, dpi), dpi) - 8) < 0.01f, "DPI resize grip roundtrip");
            check(core::hitTest(core::toDip(core::toPixels(4, dpi), dpi), 100, 640, 420, state) == core::Hit::left, "zero opacity resize at every DPI");
        }
        struct Case { float x; float y; core::Hit expected; };
        for (auto c : {Case{4,4,core::Hit::topLeft}, Case{636,4,core::Hit::topRight},
                      Case{4,416,core::Hit::bottomLeft}, Case{636,416,core::Hit::bottomRight},
                      Case{4,100,core::Hit::left}, Case{636,100,core::Hit::right},
                      Case{100,4,core::Hit::top}, Case{100,416,core::Hit::bottom},
                      Case{100,24,core::Hit::drag}, Case{100,100,core::Hit::content},
                      Case{-1,100,core::Hit::outside}, Case{640,100,core::Hit::outside},
                      Case{100,420,core::Hit::outside}})
            check(core::hitTest(c.x, c.y, 640, 420, state) == c.expected, "edge/corner/header/content hit");
        state.toggleVisible();
        check(core::hitTest(4,4,640,420,state) == core::Hit::outside, "hidden is not interactive");
        check(core::surfaceBytes(3840,2160) == 33177600, "4K surface arithmetic");
        check(core::surfaceBytes(1,1) == 4, "minimal surface");
        for (auto size : {std::pair{-1,100}, std::pair{0,100}, std::pair{100,0},
                          std::pair{16384,16384}, std::pair{std::numeric_limits<int>::max(),2}}) {
            bool rejected = false;
            try { (void)core::surfaceBytes(size.first, size.second); }
            catch (const std::invalid_argument&) { rejected = true; }
            check(rejected, "invalid/overflow/excessive surface rejected");
        }
        std::cout << checks << " native state assertions passed\n";
        return 0;
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
}
