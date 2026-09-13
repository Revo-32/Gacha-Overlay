#pragma once
#include <algorithm>

namespace core {
struct MediaBox {float width,height;};
// Full's large thumbnail envelope, in logical DIPs. Source pixels determine
// aspect and conversion quality, never the user's chosen display size.
inline MediaBox fitMedia(float width,float height,float sourceWidth,float sourceHeight) {
    if (width<=0 || height<=0 || sourceWidth<=0 || sourceHeight<=0) return {};
    const auto scale=std::min(width/sourceWidth,height/sourceHeight);
    return {sourceWidth*scale,sourceHeight*scale};
}
inline MediaBox chatMediaBox(float viewportWidth,float sourceWidth,float sourceHeight) {
    return fitMedia(std::min(viewportWidth,360.0f),270.0f,sourceWidth,sourceHeight);
}
}
