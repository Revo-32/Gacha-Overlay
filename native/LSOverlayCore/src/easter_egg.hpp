#pragma once
#include <windows.h>
#include <objidl.h>
#include <gdiplus.h>
#include <wrl/client.h>
#include <memory>

namespace core {
class EasterEgg final {
public:
    EasterEgg() = default;
    ~EasterEgg();
    EasterEgg(const EasterEgg&) = delete;
    EasterEgg& operator=(const EasterEgg&) = delete;
    void show(HINSTANCE instance, HWND monitorAnchor);
private:
    static LRESULT CALLBACK windowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    LRESULT dispatch(HWND window, UINT message, WPARAM wparam, LPARAM lparam) noexcept;
    void close() noexcept;
    void load(HINSTANCE instance);
    void release() noexcept;
    HWND window_ = nullptr;
    ULONG_PTR gdiplusToken_ = 0;
    Microsoft::WRL::ComPtr<IStream> stream_;
    std::unique_ptr<Gdiplus::Image> image_;
};
}
