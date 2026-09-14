#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <stdexcept>

namespace core {
enum class Hit { outside, content, drag, left, right, top, bottom, topLeft, topRight, bottomLeft, bottomRight, through };

struct ShellState {
    bool visible = true;
    bool locked = false;
    float backgroundOpacity = 0.85f;
    void toggleVisible() noexcept { visible = !visible; }
    void toggleLocked() noexcept { locked = !locked; }
    bool shouldShow(bool foregroundOnly, bool targetGameForeground) const noexcept {
        return visible && (!foregroundOnly || targetGameForeground);
    }
};

inline float toDip(int pixels, unsigned dpi) noexcept {
    return static_cast<float>(pixels) * 96.0f / static_cast<float>(dpi == 0 ? 96 : dpi);
}
inline int toPixels(float dip, unsigned dpi) noexcept {
    return static_cast<int>(std::lround(dip * static_cast<float>(dpi == 0 ? 96 : dpi) / 96.0f));
}
inline Hit hitTest(float x, float y, float width, float height, const ShellState& state) noexcept {
    if (!state.visible || x < 0 || y < 0 || x >= width || y >= height) return Hit::outside;
    if (state.locked) return Hit::through;
    constexpr float grip = 8.0f;
    const bool left = x < grip, right = x >= width - grip;
    const bool top = y < grip, bottom = y >= height - grip;
    if (top && left) return Hit::topLeft;
    if (top && right) return Hit::topRight;
    if (bottom && left) return Hit::bottomLeft;
    if (bottom && right) return Hit::bottomRight;
    if (left) return Hit::left;
    if (right) return Hit::right;
    if (top) return Hit::top;
    if (bottom) return Hit::bottom;
    return y < 48.0f ? Hit::drag : Hit::content;
}

// Allocation guard, not a display-quality downsample. Oversized surfaces fail explicitly.
inline std::uint32_t surfaceBytes(int width, int height) {
    if (width < 1 || height < 1 || width > 16384 || height > 16384)
        throw std::invalid_argument("Invalid native surface dimensions");
    const auto bytes = static_cast<std::uint64_t>(width) * static_cast<std::uint64_t>(height) * 4;
    if (bytes > 512ULL * 1024 * 1024) throw std::invalid_argument("Native surface allocation exceeds safety budget");
    return static_cast<std::uint32_t>(bytes);
}
}
