#pragma once
#include <d2d1.h>
#include <dwrite_3.h>
#include <wrl/client.h>
#include <vector>

namespace core {
struct GlyphOutline {
    Microsoft::WRL::ComPtr<ID2D1PathGeometry> geometry;
    D2D1_POINT_2F origin;
};
// Device-independent, per-layout geometry. Color glyph layers keep their native
// color-font rendering; only monochrome text receives an outline.
std::vector<GlyphOutline> buildTextOutline(IDWriteFactory* textFactory,
    ID2D1Factory* geometryFactory, IDWriteTextLayout* layout);
}
