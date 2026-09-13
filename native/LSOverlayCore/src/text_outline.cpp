#include "text_outline.hpp"
#include "renderer.hpp"
#include <wrl/implements.h>

using Microsoft::WRL::ComPtr;
namespace core {
namespace {
class OutlineCollector final : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,IDWriteTextRenderer> {
public:
    OutlineCollector(IDWriteFactory4* text, ID2D1Factory* geometry) : text_(text), geometry_(geometry) {}
    std::vector<GlyphOutline> paths;
    HRESULT STDMETHODCALLTYPE IsPixelSnappingDisabled(void*, BOOL* disabled) override { *disabled = TRUE; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetCurrentTransform(void*, DWRITE_MATRIX* matrix) override {
        *matrix = {1,0,0,1,0,0}; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPixelsPerDip(void*, FLOAT* scale) override { *scale = 1; return S_OK; }
    HRESULT STDMETHODCALLTYPE DrawGlyphRun(void*, FLOAT x, FLOAT y, DWRITE_MEASURING_MODE mode,
        const DWRITE_GLYPH_RUN* run, const DWRITE_GLYPH_RUN_DESCRIPTION* description, IUnknown*) override {
        try {
            ComPtr<IDWriteColorGlyphRunEnumerator1> layers;
            constexpr auto formats = static_cast<DWRITE_GLYPH_IMAGE_FORMATS>(
                DWRITE_GLYPH_IMAGE_FORMATS_TRUETYPE | DWRITE_GLYPH_IMAGE_FORMATS_CFF |
                DWRITE_GLYPH_IMAGE_FORMATS_COLR | DWRITE_GLYPH_IMAGE_FORMATS_SVG |
                DWRITE_GLYPH_IMAGE_FORMATS_PNG | DWRITE_GLYPH_IMAGE_FORMATS_JPEG |
                DWRITE_GLYPH_IMAGE_FORMATS_TIFF | DWRITE_GLYPH_IMAGE_FORMATS_PREMULTIPLIED_B8G8R8A8);
            const auto status = text_->TranslateColorGlyphRun({x,y},run,description,formats,mode,nullptr,0,&layers);
            if (status == DWRITE_E_NOCOLOR) return append(run,x,y);
            if (FAILED(status)) return status;
            BOOL more = FALSE;
            for (;;) {
                const auto moved = layers->MoveNext(&more);
                if (FAILED(moved)) return moved;
                if (!more) break;
                const DWRITE_COLOR_GLYPH_RUN1* layer = nullptr;
                const auto current = layers->GetCurrentRun(&layer);
                if (FAILED(current)) return current;
                if (layer->paletteIndex == 0xffff &&
                    (layer->glyphImageFormat == DWRITE_GLYPH_IMAGE_FORMATS_TRUETYPE ||
                     layer->glyphImageFormat == DWRITE_GLYPH_IMAGE_FORMATS_CFF)) {
                    const auto added = append(&layer->glyphRun,layer->baselineOriginX,layer->baselineOriginY);
                    if (FAILED(added)) return added;
                }
            }
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; } // Never unwind a C++ exception through COM.
    }
    HRESULT STDMETHODCALLTYPE DrawUnderline(void*, FLOAT, FLOAT, const DWRITE_UNDERLINE*, IUnknown*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE DrawStrikethrough(void*, FLOAT, FLOAT, const DWRITE_STRIKETHROUGH*, IUnknown*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE DrawInlineObject(void*, FLOAT, FLOAT, IDWriteInlineObject*, BOOL, BOOL, IUnknown*) override { return S_OK; } // image has no glyph outline
private:
    HRESULT append(const DWRITE_GLYPH_RUN* run, FLOAT x, FLOAT y) {
        ComPtr<ID2D1PathGeometry> path;
        auto result = geometry_->CreatePathGeometry(&path); if (FAILED(result)) return result;
        ComPtr<ID2D1GeometrySink> sink;
        result = path->Open(&sink); if (FAILED(result)) return result;
        result = run->fontFace->GetGlyphRunOutline(run->fontEmSize,run->glyphIndices,run->glyphAdvances,
            run->glyphOffsets,run->glyphCount,run->isSideways,run->bidiLevel%2,sink.Get());
        if (FAILED(result)) return result;
        result = sink->Close(); if (FAILED(result)) return result;
        paths.push_back({std::move(path),{x,y}}); return S_OK;
    }
    ComPtr<IDWriteFactory4> text_;
    ComPtr<ID2D1Factory> geometry_;
};
}
std::vector<GlyphOutline> buildTextOutline(IDWriteFactory* textFactory,
    ID2D1Factory* geometryFactory, IDWriteTextLayout* layout) {
    ComPtr<IDWriteFactory4> modern;
    require(textFactory->QueryInterface(IID_PPV_ARGS(&modern)),"Color-aware outline support");
    auto collector = Microsoft::WRL::Make<OutlineCollector>(modern.Get(),geometryFactory);
    if (!collector) throw std::bad_alloc();
    require(layout->Draw(nullptr,collector.Get(),0,0),"Chat glyph outline layout");
    return std::move(collector->paths);
}
}
