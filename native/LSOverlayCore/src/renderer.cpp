#include "renderer.hpp"
#include "chat_view.hpp"
#include <wincodec.h>
#include <cstdio>
#include <string>

using Microsoft::WRL::ComPtr;
namespace core {
void require(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        char code[32]{};
        sprintf_s(code, " (HRESULT 0x%08lX)", static_cast<unsigned long>(result));
        throw std::runtime_error(std::string(operation) + code);
    }
}

Renderer::Renderer() {
    require(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, factory_.GetAddressOf()), "D2D factory");
    require(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
        reinterpret_cast<IUnknown**>(write_.GetAddressOf())), "DirectWrite factory");
}
Renderer::~Renderer() { discardSurface(); }

void Renderer::setConnectionStatus(std::wstring status) {
    if (connectionStatus_ == status) return;
    connectionStatus_ = std::move(status);
    layoutWidth_ = 0;
}
void Renderer::setChatSnapshot(std::shared_ptr<const Json> snapshot) {
    if (!chat_) chat_ = std::make_unique<ChatView>(write_.Get());
    chat_->setSnapshot(std::move(snapshot));
    setConnectionStatus(L"M3 합성 채팅 검증 · 실제 Discord 데이터 아님");
}

void Renderer::discardSurface() noexcept {
    if (chat_) chat_->releaseTargetResources();
    staticLayer_.Reset(); brush_.Reset(); target_.Reset();
    if (dc_ && original_ && original_ != HGDI_ERROR) SelectObject(dc_, original_);
    if (bitmap_) DeleteObject(bitmap_);
    if (dc_) DeleteDC(dc_);
    dc_ = nullptr; bitmap_ = nullptr; original_ = nullptr; pixels_ = nullptr;
    width_ = height_ = 0; dpi_ = 0;
}

void Renderer::ensureSurface(int width, int height, unsigned dpi) {
    (void)surfaceBytes(width, height);
    if (width_ == width && height_ == height && dpi_ == dpi && target_) return;
    discardSurface();
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    dc_ = CreateCompatibleDC(nullptr);
    if (!dc_) throw std::runtime_error("CreateCompatibleDC failed");
    bitmap_ = CreateDIBSection(dc_, &info, DIB_RGB_COLORS, &pixels_, nullptr, 0);
    if (!bitmap_) throw std::runtime_error("CreateDIBSection failed");
    original_ = SelectObject(dc_, bitmap_);
    if (!original_ || original_ == HGDI_ERROR) throw std::runtime_error("SelectObject failed");
    auto properties = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_SOFTWARE,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
        static_cast<float>(dpi), static_cast<float>(dpi), D2D1_RENDER_TARGET_USAGE_NONE);
    require(factory_->CreateDCRenderTarget(&properties, target_.GetAddressOf()), "D2D DC render target");
    const RECT bounds{0, 0, width, height};
    require(target_->BindDC(dc_, &bounds), "D2D BindDC");
    // ClearType subpixel coverage is invalid over arbitrary translucent backgrounds.
    target_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    require(target_->CreateSolidColorBrush(D2D1::ColorF(0xffffff), brush_.GetAddressOf()), "D2D brush");
    width_ = width; height_ = height; dpi_ = dpi;
}

void Renderer::buildLayouts(float width, bool locked, bool hotkeysAvailable) {
    if (layoutWidth_ == width && layoutLocked_ == locked && layoutHotkeys_ == hotkeysAvailable && layouts_[0]) return;
    const wchar_t* lines[] = {
        L"LS Overlay Core",
        connectionStatus_.empty() ? L"네이티브 검증 화면 · 실제 Discord 연결 없음" : connectionStatus_.c_str(),
        L"한국어 렌더링 확인",
        L"안녕하세요! 한글·영문·숫자를 선명하게 표시합니다.\n창의 폭을 바꾸면 문장이 자연스럽게 줄바꿈됩니다.\n가나다라마바사 · ABC 123 · GTA Online",
        locked ? L"잠금 상태 · 마우스 입력이 뒤 창으로 통과합니다." : L"잠금 해제 · 상단 드래그 / 가장자리 크기 조절",
        hotkeysAvailable ? L"F9 표시/숨기기  ·  F10 잠금/해제  ·  우클릭 메뉴" : L"단축키 충돌 · 기존 앱을 유지합니다. 트레이 메뉴를 사용하세요.",
        chat_ ? chatFooter_.c_str() : connectionStatus_.empty() ? L"M1 · Direct2D / DirectWrite · 네트워크 및 로그인 기능 없음" : L"M2 합성 통신 검증 · 실제 채팅 UI는 다음 단계에서 구현"
    };
    const float sizes[] = {20, 12, 16, 16, 12, 12, 11};
    for (std::size_t i = 0; i < layouts_.size(); ++i) {
        ComPtr<IDWriteTextFormat> format;
        require(write_->CreateTextFormat(L"Malgun Gothic", nullptr,
            i == 0 || i == 2 ? DWRITE_FONT_WEIGHT_SEMI_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
            DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, sizes[i], L"ko-KR", format.GetAddressOf()), "Korean text format");
        require(format->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP), "Word wrapping");
        require(format->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM, sizes[i] * 1.55f, sizes[i] * 1.16f), "Line spacing");
        ComPtr<IDWriteTextLayout> next;
        require(write_->CreateTextLayout(lines[i], static_cast<UINT32>(wcslen(lines[i])), format.Get(),
            width - 48, i == 3 ? 180.0f : 50.0f, next.GetAddressOf()), "DirectWrite layout");
        layouts_[i] = std::move(next);
    }
    layoutWidth_ = width; layoutLocked_ = locked; layoutHotkeys_ = hotkeysAvailable;
    ++layoutBuildCount_;
}

void Renderer::text(IDWriteTextLayout* layout, float x, float y, D2D1_COLOR_F color) {
    brush_->SetColor(color);
    target_->DrawTextLayout(D2D1::Point2F(x, y), layout, brush_.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
}

void Renderer::draw(HWND window, int width, int height, unsigned dpi, const ShellState& state, bool hotkeysAvailable, bool mediaOnly) {
    drawOnce(window,width,height,dpi,state,hotkeysAvailable,true,mediaOnly);
}
void Renderer::setMedia(std::shared_ptr<MediaStore> media) {
    if (!chat_) chat_ = std::make_unique<ChatView>(write_.Get());
    chat_->setMedia(std::move(media));
    staticLayer_.Reset();
}

void Renderer::drawOnce(HWND window, int width, int height, unsigned dpi, const ShellState& state, bool hotkeysAvailable, bool retryAllowed, bool mediaOnly) {
    ensureSurface(width, height, dpi);
    const float w = toDip(width, dpi), h = toDip(height, dpi);
    HRESULT result = S_OK;
    if (!mediaOnly || !staticLayer_) {
    if (chat_) {
        const auto footer = chat_->scrolling().following() ? L"최신 메시지 · "+chat_->fontName()+L" · 우클릭: 글꼴/크기/멘션 배경" :
            L"새 메시지 "+std::to_wstring(chat_->scrolling().unread())+L"개 · 여기를 클릭하거나 End: 최신으로";
        if (chatFooter_ != footer) { chatFooter_ = footer; layoutWidth_ = 0; }
    }
    buildLayouts(w, state.locked, hotkeysAvailable);
    target_->BeginDraw();
    target_->SetTransform(D2D1::Matrix3x2F::Identity());
    target_->Clear(D2D1::ColorF(0, 0.0f));
    brush_->SetColor(D2D1::ColorF(0x161b22, std::clamp(state.backgroundOpacity, 0.0f, 1.0f)));
    target_->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(8, 8, w - 8, h - 8), 10, 10), brush_.Get());
    if (!state.locked) {
        // Nonzero alpha across ALL resize pixels matters: alpha=0 bypasses native
        // hit testing even when WM_NCHITTEST would otherwise return a resize edge.
        brush_->SetColor(D2D1::ColorF(0x58a6ff, 0.16f));
        for (auto rect : {D2D1::RectF(0,0,w,8), D2D1::RectF(0,h-8,w,h),
                          D2D1::RectF(0,8,8,h-8), D2D1::RectF(w-8,8,w,h-8), D2D1::RectF(8,8,w-8,48)})
            target_->FillRectangle(rect, brush_.Get());
        brush_->SetColor(D2D1::ColorF(0x58a6ff));
        target_->DrawRectangle(D2D1::RectF(0.5f,0.5f,w-0.5f,h-0.5f), brush_.Get(), 1.0f);
        for (float offset : {6.0f, 10.0f, 14.0f})
            target_->DrawLine(D2D1::Point2F(w-offset,h-3), D2D1::Point2F(w-3,h-offset), brush_.Get());
    }
    text(layouts_[0].Get(), 24, 13, D2D1::ColorF(0xf0f6fc));
    text(layouts_[1].Get(), 24, 57, D2D1::ColorF(0x8b949e));
    if (chat_) {
        chat_->draw(target_.Get(),D2D1::RectF(24,96,w-24,h-60));
        text(layouts_[6].Get(),24,h-35,D2D1::ColorF(0x8b949e));
    } else {
    text(layouts_[2].Get(), 24, 103, D2D1::ColorF(0x58a6ff));
    text(layouts_[3].Get(), 24, 139, D2D1::ColorF(0xf0f6fc));
    brush_->SetColor(D2D1::ColorF(0x30363d));
    target_->DrawLine(D2D1::Point2F(24,h-119), D2D1::Point2F(w-24,h-119), brush_.Get());
    text(layouts_[4].Get(), 24, h-104, D2D1::ColorF(0xc9d1d9));
    text(layouts_[5].Get(), 24, h-73, D2D1::ColorF(0xc9d1d9));
    text(layouts_[6].Get(), 24, h-35, D2D1::ColorF(0x8b949e));
    }
    result = target_->EndDraw();
    if (SUCCEEDED(result) && chat_ && chat_->hasMedia()) {
        // Preserve the exact physical-resolution text/background result. Future
        // animation frames need one bitmap blit, not repeated glyph geometry.
        const auto properties = D2D1::BitmapProperties(D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),static_cast<float>(dpi),static_cast<float>(dpi));
        if (!staticLayer_) require(target_->CreateBitmap(D2D1::SizeU(static_cast<UINT32>(width),static_cast<UINT32>(height)),pixels_,static_cast<UINT32>(width)*4,properties,&staticLayer_),"Static native media layer");
        else require(staticLayer_->CopyFromMemory(nullptr,pixels_,static_cast<UINT32>(width)*4),"Refresh static native layer");
    }
    }
    if (SUCCEEDED(result) && chat_ && chat_->hasMedia()) {
        target_->BeginDraw(); target_->SetTransform(D2D1::Matrix3x2F::Identity());
        target_->Clear(D2D1::ColorF(0,0.0f));
        target_->DrawBitmap(staticLayer_.Get(),D2D1::RectF(0,0,w,h),1,D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR);
        chat_->drawMedia(target_.Get()); result = target_->EndDraw();
    }
    if (result == D2DERR_RECREATE_TARGET && retryAllowed) {
        discardSurface();
        drawOnce(window,width,height,dpi,state,hotkeysAvailable,false,false);
        return;
    }
    require(result, "D2D EndDraw");
    RECT bounds{};
    if (!GetWindowRect(window, &bounds)) throw std::runtime_error("GetWindowRect failed");
    POINT destination{bounds.left, bounds.top}, source{0,0};
    SIZE size{width,height};
    BLENDFUNCTION blend{AC_SRC_OVER,0,255,AC_SRC_ALPHA};
    if (!UpdateLayeredWindow(window, nullptr, &destination, &size, dc_, &source, 0, &blend, ULW_ALPHA))
        throw std::runtime_error("UpdateLayeredWindow failed: " + std::to_string(GetLastError()));
    ++renderCount_;
}

unsigned Renderer::alphaAt(int x, int y) const {
    if (!pixels_ || x < 0 || y < 0 || x >= width_ || y >= height_) throw std::out_of_range("Pixel outside backing surface");
    const auto* bytes = static_cast<const unsigned char*>(pixels_);
    return bytes[(static_cast<std::size_t>(y) * width_ + x) * 4 + 3];
}

void Renderer::savePng(const std::filesystem::path& path) const {
    if (!pixels_) throw std::runtime_error("No rendered surface to capture");
    ComPtr<IWICImagingFactory> wic;
    require(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(wic.GetAddressOf())), "WIC factory");
    ComPtr<IWICBitmap> source;
    require(wic->CreateBitmapFromMemory(static_cast<UINT>(width_), static_cast<UINT>(height_), GUID_WICPixelFormat32bppPBGRA,
        static_cast<UINT>(width_) * 4, surfaceBytes(width_,height_), static_cast<BYTE*>(pixels_), source.GetAddressOf()), "WIC capture bitmap");
    ComPtr<IWICFormatConverter> converter;
    require(wic->CreateFormatConverter(converter.GetAddressOf()), "WIC converter");
    require(converter->Initialize(source.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom), "WIC alpha conversion");
    ComPtr<IWICStream> stream;
    require(wic->CreateStream(stream.GetAddressOf()), "WIC stream");
    require(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE), "WIC capture output");
    ComPtr<IWICBitmapEncoder> encoder;
    require(wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, encoder.GetAddressOf()), "WIC PNG encoder");
    require(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache), "PNG initialization");
    ComPtr<IWICBitmapFrameEncode> frame;
    require(encoder->CreateNewFrame(frame.GetAddressOf(), nullptr), "PNG frame");
    require(frame->Initialize(nullptr), "PNG frame initialization");
    require(frame->SetSize(static_cast<UINT>(width_), static_cast<UINT>(height_)), "PNG dimensions");
    WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
    require(frame->SetPixelFormat(&format), "PNG pixel format");
    require(frame->WriteSource(converter.Get(), nullptr), "PNG write pixels");
    require(frame->Commit(), "PNG frame commit");
    require(encoder->Commit(), "PNG encoder commit");
}
}
