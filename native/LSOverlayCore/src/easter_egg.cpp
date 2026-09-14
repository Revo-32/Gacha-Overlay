#include "easter_egg.hpp"
#include <cstring>
#include <stdexcept>

namespace core {
namespace {
constexpr wchar_t windowClass[] = L"LSOverlayCore.EasterEgg";
constexpr UINT_PTR closeTimer = 1;
}

EasterEgg::~EasterEgg() { close(); }

void EasterEgg::load(HINSTANCE instance) {
    Gdiplus::GdiplusStartupInput input;
    if (Gdiplus::GdiplusStartup(&gdiplusToken_, &input, nullptr) != Gdiplus::Ok)
        throw std::runtime_error("Easter egg graphics startup failed");
    const auto resource = FindResourceW(instance, MAKEINTRESOURCEW(102), RT_RCDATA);
    const auto loaded = resource ? LoadResource(instance, resource) : nullptr;
    const auto source = loaded ? LockResource(loaded) : nullptr;
    const DWORD size = resource ? SizeofResource(instance, resource) : 0;
    const auto copy = size ? GlobalAlloc(GMEM_MOVEABLE, size) : nullptr;
    if (!source || !copy) { release(); throw std::runtime_error("Easter egg resource unavailable"); }
    void* destination = GlobalLock(copy);
    if (!destination) { GlobalFree(copy); release(); throw std::runtime_error("Easter egg resource lock failed"); }
    memcpy(destination, source, size);
    GlobalUnlock(copy);
    if (FAILED(CreateStreamOnHGlobal(copy, TRUE, stream_.ReleaseAndGetAddressOf()))) {
        GlobalFree(copy); release(); throw std::runtime_error("Easter egg stream failed");
    }
    image_.reset(Gdiplus::Image::FromStream(stream_.Get(), FALSE));
    if (!image_ || image_->GetLastStatus() != Gdiplus::Ok) {
        release(); throw std::runtime_error("Easter egg image decode failed");
    }
}

void EasterEgg::release() noexcept {
    image_.reset();
    stream_.Reset();
    if (gdiplusToken_) { Gdiplus::GdiplusShutdown(gdiplusToken_); gdiplusToken_ = 0; }
}

void EasterEgg::show(HINSTANCE instance, HWND monitorAnchor) {
    close();
    load(instance);
    WNDCLASSEXW klass{};
    klass.cbSize = sizeof(klass);
    klass.hInstance = instance;
    klass.lpszClassName = windowClass;
    klass.lpfnWndProc = windowProc;
    klass.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    if (!RegisterClassExW(&klass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        release(); throw std::runtime_error("Easter egg window registration failed");
    }
    MONITORINFO monitor{sizeof(monitor)};
    if (!GetMonitorInfoW(MonitorFromWindow(monitorAnchor, MONITOR_DEFAULTTONEAREST), &monitor)) {
        release(); throw std::runtime_error("Easter egg monitor unavailable");
    }
    window_ = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
        windowClass, L"", WS_POPUP, monitor.rcMonitor.left, monitor.rcMonitor.top,
        monitor.rcMonitor.right-monitor.rcMonitor.left, monitor.rcMonitor.bottom-monitor.rcMonitor.top,
        nullptr, nullptr, instance, this);
    if (!window_) { release(); throw std::runtime_error("Easter egg window creation failed"); }
    SetWindowPos(window_, HWND_TOPMOST, monitor.rcMonitor.left, monitor.rcMonitor.top,
        monitor.rcMonitor.right-monitor.rcMonitor.left, monitor.rcMonitor.bottom-monitor.rcMonitor.top,
        SWP_NOACTIVATE | SWP_SHOWWINDOW);
    if (!SetTimer(window_, closeTimer, 2000, nullptr)) close();
}

void EasterEgg::close() noexcept {
    if (window_) DestroyWindow(window_);
    release();
}

LRESULT CALLBACK EasterEgg::windowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) noexcept {
    auto* self = reinterpret_cast<EasterEgg*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        self = static_cast<EasterEgg*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    return self ? self->dispatch(window, message, wparam, lparam) : DefWindowProcW(window, message, wparam, lparam);
}

LRESULT EasterEgg::dispatch(HWND window, UINT message, WPARAM wparam, LPARAM lparam) noexcept {
    switch (message) {
    case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
    case WM_NCHITTEST: return HTTRANSPARENT;
    case WM_ERASEBKGND: return 1;
    case WM_PAINT: {
        PAINTSTRUCT paint{};
        const auto dc = BeginPaint(window, &paint);
        RECT bounds{}; GetClientRect(window, &bounds);
        Gdiplus::Graphics graphics(dc);
        graphics.SetInterpolationMode(Gdiplus::InterpolationModeLowQuality);
        if (image_) graphics.DrawImage(image_.get(), 0, 0, bounds.right, bounds.bottom);
        EndPaint(window, &paint);
        return 0;
    }
    case WM_TIMER: close(); return 0;
    case WM_NCDESTROY:
        KillTimer(window, closeTimer);
        SetWindowLongPtrW(window, GWLP_USERDATA, 0);
        window_ = nullptr;
        release();
        return DefWindowProcW(window, message, wparam, lparam);
    default: return DefWindowProcW(window, message, wparam, lparam);
    }
}
}
