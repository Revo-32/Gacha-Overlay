#pragma once
#include "shell_state.hpp"
#include <windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>
#include <array>
#include <filesystem>
#include <string>

namespace core {
void require(HRESULT result, const char* operation);

// Minimal per-pixel-alpha feasibility path. CPU-backed DC rendering is measured
// explicitly; this is not a claim that full-surface uploads are optimal for GIFs.
class Renderer final {
public:
    Renderer();
    ~Renderer();
    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;
    void draw(HWND window, int width, int height, unsigned dpi, const ShellState& state, bool hotkeysAvailable);
    void savePng(const std::filesystem::path& path) const;
    void discardSurface() noexcept;
    void setConnectionStatus(std::wstring status);
    [[nodiscard]] unsigned alphaAt(int x, int y) const;
    [[nodiscard]] std::uint64_t renderCount() const noexcept { return renderCount_; }
    [[nodiscard]] std::uint64_t layoutBuildCount() const noexcept { return layoutBuildCount_; }
    [[nodiscard]] std::uint32_t backingBytes() const { return bitmap_ ? surfaceBytes(width_, height_) : 0; }
private:
    void drawOnce(HWND window, int width, int height, unsigned dpi, const ShellState& state, bool hotkeysAvailable, bool retryAllowed);
    void ensureSurface(int width, int height, unsigned dpi);
    void buildLayouts(float width, bool locked, bool hotkeysAvailable);
    void text(IDWriteTextLayout* layout, float x, float y, D2D1_COLOR_F color);
    Microsoft::WRL::ComPtr<ID2D1Factory> factory_;
    Microsoft::WRL::ComPtr<IDWriteFactory> write_;
    Microsoft::WRL::ComPtr<ID2D1DCRenderTarget> target_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> brush_;
    std::array<Microsoft::WRL::ComPtr<IDWriteTextLayout>, 7> layouts_;
    HDC dc_ = nullptr;
    HBITMAP bitmap_ = nullptr;
    HGDIOBJ original_ = nullptr;
    void* pixels_ = nullptr;
    int width_ = 0, height_ = 0;
    unsigned dpi_ = 0;
    float layoutWidth_ = 0;
    bool layoutLocked_ = false, layoutHotkeys_ = false;
    std::wstring connectionStatus_;
    std::uint64_t renderCount_ = 0, layoutBuildCount_ = 0;
};
}
