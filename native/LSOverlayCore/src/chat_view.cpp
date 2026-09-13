#include "chat_view.hpp"
#include "renderer.hpp"
#include "transport.hpp"
#include <filesystem>
#include <array>
#include <cstdio>
#include <bit>

using Microsoft::WRL::ComPtr;
namespace core {
namespace {
std::wstring optionalText(yyjson_val* value, const char* name) {
    auto* item = field(value,name); return yyjson_is_str(item) ? widen(stringValue(item)) : L"";
}
std::wstring localTime(yyjson_val* value) {
    auto* timestamp = field(value,"createdAt");
    if (!yyjson_is_str(timestamp)) return {};
    const auto ticks = static_cast<std::uint64_t>(parseUtc(stringValue(timestamp)));
    FILETIME utc{static_cast<DWORD>(ticks),static_cast<DWORD>(ticks>>32)}, local{}; SYSTEMTIME time{};
    if (!FileTimeToLocalFileTime(&utc,&local) || !FileTimeToSystemTime(&local,&time)) return {};
    wchar_t text[8]{}; swprintf_s(text,L"%02u:%02u",time.wHour,time.wMinute); return text;
}
struct FontPair { const wchar_t* author; const wchar_t* body; const wchar_t* name; DWRITE_FONT_WEIGHT bodyWeight; DWRITE_FONT_WEIGHT authorWeight = DWRITE_FONT_WEIGHT_BOLD; };
constexpr FontPair fontPairs[] = {
    {L"KIMM_Bold",L"KIMM_Light",L"한국기계연구원",DWRITE_FONT_WEIGHT_LIGHT},
    {L"Pretendard Variable",L"Pretendard Variable",L"Pretendard",DWRITE_FONT_WEIGHT_NORMAL,DWRITE_FONT_WEIGHT_SEMI_BOLD},
    {L"Wanted Sans Variable",L"Wanted Sans Variable",L"Wanted Sans",DWRITE_FONT_WEIGHT_MEDIUM},
    {L"Cafe24 PRO Slim Max",L"Cafe24 PRO Slim Fit",L"GTA 레거시",DWRITE_FONT_WEIGHT_NORMAL},
    {L"조선굴림체",L"조선굴림체",L"조선굴림",DWRITE_FONT_WEIGHT_NORMAL,DWRITE_FONT_WEIGHT_NORMAL}
};
}
ChatView::ChatView(IDWriteFactory* factory) : factory_(factory) {
    ComPtr<IDWriteFactory5> modern; require(factory_.As(&modern),"DirectWrite private fonts support");
    ComPtr<IDWriteFontSetBuilder1> builder; require(modern->CreateFontSetBuilder(&builder),"Private font set builder");
    ComPtr<IDWriteFontSet> system; require(modern->GetSystemFontSet(&system),"System font fallback set");
    require(builder->AddFontSet(system.Get()),"System/emoji fallback fonts");
    std::wstring executable(32768,L'\0'); const auto length = GetModuleFileNameW(nullptr,executable.data(),static_cast<DWORD>(executable.size()));
    if (length == 0 || length >= executable.size()) throw std::runtime_error("Core executable path unavailable");
    executable.resize(length);
    const auto directory = std::filesystem::path(executable).parent_path()/L"fonts";
    for (const auto* name : {L"KIMM_Bold.ttf",L"KIMM_Light.ttf",L"PretendardVariable.ttf",L"WantedSansVariable.ttf",L"Cafe24PROSlimMax.ttf",L"Cafe24PROSlimFit.ttf",L"ChosunGu.TTF"}) {
        ComPtr<IDWriteFontFile> file; require(factory_->CreateFontFileReference((directory/name).c_str(),nullptr,&file),"Bundled font file");
        require(builder->AddFontFile(file.Get()),"Bundled font faces");
    }
    ComPtr<IDWriteFontSet> set; require(builder->CreateFontSet(&set),"Private font set");
    ComPtr<IDWriteFontCollection1> collection; require(modern->CreateFontCollectionFromFontSet(set.Get(),&collection),"Private font collection");
    require(collection.As(&fonts_),"Font collection interface");
}
ChatBlock ChatView::runs(yyjson_val* values, float size, std::wstring prefix) const {
    if (!yyjson_is_arr(values)) throw std::runtime_error("Invalid Core runs");
    ChatBlock block; block.size = size; block.text = std::move(prefix);
    std::size_t index = 0,count = 0; yyjson_val* run = nullptr;
    yyjson_arr_foreach(values,index,count,run) {
        const auto kind = textField(run,"kind"); auto text = widen(textField(run,"text"));
        // Explicit fallback until M4 assets: preserve the emoji identity/name.
        if (kind == "CustomEmoji" && !text.empty() && text.front() != L':') text = L":"+text+L":";
        const auto start = static_cast<UINT32>(block.text.size()); block.text += text;
        if (kind == "Mention") block.spans.push_back({{start,static_cast<UINT32>(text.size())},
            yyjson_is_true(field(run,"isSelf")) ? 0x3fb950U : 0xc9d1d9U,yyjson_is_true(field(run,"isSelf"))});
    }
    return block;
}
ChatRow ChatView::makeRow(yyjson_val* value) const {
    ChatRow row; row.id = textField(value,"id");
    row.fingerprint = std::string(textField(value,"presentationHash"));
    row.attention = textField(value,"attention"); row.header = yyjson_is_true(field(value,"showAuthorHeader"));
    auto* author = field(value,"author");
    if (row.header) {
        ChatBlock header; header.size = fontSize_; header.author = true;
        const auto icon = optionalText(author,"iconUnicode");
        const auto name = widen(textField(author,"displayName"));
        header.text = rolePosition_ == 0 ? (icon.empty() ? L"" : icon+L" ")+name :
            rolePosition_ == 1 ? name+(icon.empty() ? L"" : L" "+icon) : name;
        if (rolePosition_ == 2) row.icon = icon;
        auto* color = field(author,"color"); if (yyjson_is_uint(color) && yyjson_get_uint(color) <= 0xffffff) header.color = static_cast<std::uint32_t>(yyjson_get_uint(color));
        row.blocks.push_back(std::move(header)); row.time = localTime(value);
    }
    auto* reply = field(value,"reply");
    if (yyjson_is_obj(reply)) {
        auto prefix = L"↳ "+optionalText(reply,"authorName")+L": ";
        auto block = runs(field(reply,"runs"),fontSize_*0.85f,std::move(prefix)); block.color = 0x8b949e;
        if (textField(reply,"status") != "resolved") block.text += L"답글 원문을 불러올 수 없습니다.";
        row.blocks.push_back(std::move(block));
    }
    auto body = runs(field(value,"runs"),fontSize_); body.body = true;
    if (!body.text.empty()) row.blocks.push_back(std::move(body));
    auto* forwarded = field(value,"forwarded");
    if (!yyjson_is_arr(forwarded)) throw std::runtime_error("Invalid Core forwarded content");
    std::size_t index = 0,count = 0; yyjson_val* forward = nullptr;
    yyjson_arr_foreach(forwarded,index,count,forward) {
        row.blocks.push_back(runs(field(forward,"runs"),fontSize_,L"전달된 메시지\n"));
        auto* assets = field(forward,"media");
        if (!yyjson_is_arr(assets)) throw std::runtime_error("Invalid forwarded media metadata");
        std::size_t assetIndex = 0,assetCount = 0; yyjson_val* asset = nullptr;
        yyjson_arr_foreach(assets,assetIndex,assetCount,asset) {
            ChatBlock block; block.size = fontSize_*0.85f; block.color = 0x8b949e;
            block.text = L"[전달된 미디어: "+optionalText(asset,"name")+L" · M4 재생 구현 대기]";
            row.blocks.push_back(std::move(block));
        }
    }
    auto* media = field(value,"media");
    if (!yyjson_is_arr(media)) throw std::runtime_error("Invalid Core media metadata");
    yyjson_val* item = nullptr;
    yyjson_arr_foreach(media,index,count,item) {
        ChatBlock block; block.size = fontSize_*0.85f; block.color = 0x8b949e;
        block.text = L"[미디어: "+optionalText(item,"name")+L" · M4 재생 구현 대기]"; row.blocks.push_back(std::move(block));
    }
    auto* reactions = field(value,"reactions");
    if (!yyjson_is_arr(reactions)) throw std::runtime_error("Invalid Core reactions");
    ChatBlock reactionBlock; reactionBlock.size = fontSize_*0.85f; reactionBlock.color = 0xc9d1d9;
    yyjson_arr_foreach(reactions,index,count,item) {
        auto* emoji = field(item,"emoji");
        const auto text = widen(textField(emoji,"text"));
        reactionBlock.text += (textField(emoji,"kind") == "CustomEmoji" ? L":"+text+L":" : text) + L" "+std::to_wstring(numberField(item,"count"))+L"   ";
    }
    if (!reactionBlock.text.empty()) row.blocks.push_back(std::move(reactionBlock));
    auto* details = field(value,"details");
    if (yyjson_is_arr(details)) yyjson_arr_foreach(details,index,count,item) {
        const auto kind = textField(item,"kind");
        const auto* label = kind == "voice" ? L"음성 메시지: " : kind == "attachment" ? L"첨부 파일: " :
            kind == "poll" ? L"투표: " : kind == "components" ? L"구성요소: " : L"임베드: ";
        ChatBlock detail; detail.size = fontSize_*0.85f; detail.color = 0x8b949e;
        detail.text = label+widen(textField(item,"text")); row.blocks.push_back(std::move(detail));
    }
    if (row.blocks.empty()) { ChatBlock fallback; fallback.text = L"[표시 가능한 본문 없음]"; fallback.size = fontSize_; row.blocks.push_back(std::move(fallback)); }
    return row;
}
void ChatView::setSnapshot(std::shared_ptr<const Json> snapshot) {
    const auto anchor = captureReadingAnchor();
    auto* root = snapshot->root(); auto* chat = field(root,"chat");
    if (!yyjson_is_arr(chat) || numberField(root,"protocolVersion") != 1) throw std::runtime_error("Invalid chat snapshot");
    std::vector<ChatRow> next; next.reserve(yyjson_arr_size(chat));
    std::unordered_set<std::string> ids;
    std::size_t index = 0,count = 0; yyjson_val* value = nullptr;
    yyjson_arr_foreach(chat,index,count,value) {
        const std::string id(textField(value,"id")); const auto hash = textField(value,"presentationHash");
        if (id.empty() || hash.size() != 64 || !ids.insert(id).second) throw std::runtime_error("Invalid chat identity/fingerprint");
        auto previous = std::find_if(rows_.begin(),rows_.end(),[&](const auto& row) { return row.id == id && row.fingerprint == hash; });
        next.push_back(previous != rows_.end() ? *previous : makeRow(value));
    }
    newGeneration_ = generation_ != textField(root,"generation"); generation_ = textField(root,"generation");
    releaseTargetResources();
    snapshot_ = std::move(snapshot); rows_ = std::move(next); changed_ = true;
    pendingAnchor_ = anchor;
}
std::optional<ChatView::TextAnchor> ChatView::captureReadingAnchor() const {
    if (scroll_.following()) return {};
    const auto anchor = scroll_.anchor();
    const auto row = std::find_if(rows_.begin(),rows_.end(),[&](const auto& item) { return item.id == anchor.id; });
    if (row == rows_.end()) return {};
    for (std::size_t i = 0; i < row->blocks.size(); ++i) {
        const auto& block = row->blocks[i];
        if (!block.layout || anchor.within < block.y || anchor.within >= block.y+block.height) continue;
        BOOL trailing = FALSE, inside = FALSE; DWRITE_HIT_TEST_METRICS hit{};
        if (SUCCEEDED(block.layout->HitTestPoint(0,anchor.within-block.y,&trailing,&inside,&hit)))
            return TextAnchor{row->id,row->fingerprint,i,hit.textPosition,anchor.within-block.y-hit.top};
    }
    return {};
}
void ChatView::layout(ChatBlock& block, float width) {
    block.outline.clear(); block.outlineBuilt = false;
    const auto key = static_cast<std::uint64_t>(std::bit_cast<std::uint32_t>(block.size)) | (static_cast<std::uint64_t>(block.author)<<32);
    auto& format = formats_[key];
    if (!format) {
    const auto& pair = fontPairs[preset_];
    const auto* family = block.author ? pair.author : pair.body;
    UINT32 index = 0; BOOL found = FALSE; require(fonts_->FindFamilyName(family,&index,&found),"Find bundled family");
    // KIMM's metadata can expose either PostScript or family names across OS versions.
    if (!found && preset_ == 0) family = block.author ? L"KIMM" : L"KIMM L";
    require(fonts_->FindFamilyName(family,&index,&found),"Resolve bundled family");
    if (!found) throw std::runtime_error("Requested bundled font family unavailable: "+narrow(family));
    require(factory_->CreateTextFormat(family,fonts_.Get(),block.author ? pair.authorWeight : pair.bodyWeight,
        DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,block.size,L"ko-KR",&format),"Chat text format");
    require(format->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP),"Chat wrapping");
    const float line = std::ceil(block.size*1.42f);
    require(format->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM,line,block.size*1.12f),"Chat line spacing");
    }
    require(factory_->CreateTextLayout(block.text.c_str(),static_cast<UINT32>(block.text.size()),format.Get(),std::max(width,1.0f),1e7f,&block.layout),"Chat layout");
    for (const auto& span : block.spans) if (span.bold) require(block.layout->SetFontWeight(DWRITE_FONT_WEIGHT_BOLD,span.range),"Mention weight");
    DWRITE_TEXT_METRICS metrics{}; require(block.layout->GetMetrics(&metrics),"Chat layout measurement"); block.height = metrics.height;
    ++layoutBuilds_;
}
void ChatView::arrange(float width, float height) {
    if (!changed_ && width_ == width && viewportHeight_ == height) return;
    const auto anchor = pendingAnchor_ ? pendingAnchor_ : captureReadingAnchor();
    const bool reflow = width_ != width;
    std::vector<ChatExtent> extents; extents.reserve(rows_.size());
    for (auto& row : rows_) {
        float y = row.header ? 18.0f : 2.0f;
        for (auto& block : row.blocks) {
            if (reflow || !block.layout) layout(block,width-(block.author ? (row.time.empty() ? 0 : 58)+(row.icon.empty() ? 0 : 24) : 0));
            block.y = y; y += block.height+2;
        }
        if (row.header && (reflow || !row.timeLayout)) {
            ChatBlock time; time.text = row.time; time.size = fontSize_*0.8f; layout(time,56); row.timeLayout = std::move(time.layout);
        }
        if (!row.icon.empty() && (reflow || !row.iconLayout)) {
            ChatBlock icon; icon.text = row.icon; icon.size = fontSize_; layout(icon,24); row.iconLayout = std::move(icon.layout);
        }
        row.height = y+4; extents.push_back({row.id,row.height});
    }
    scroll_.setRows(std::move(extents),height,newGeneration_);
    if (anchor && !scroll_.following()) {
        const auto row = std::find_if(rows_.begin(),rows_.end(),[&](const auto& item) { return item.id == anchor->id; });
        if (row != rows_.end() && row->fingerprint == anchor->fingerprint && anchor->block < row->blocks.size()) {
            const auto& block = row->blocks[anchor->block];
            FLOAT x = 0,y = 0; DWRITE_HIT_TEST_METRICS hit{};
            if (SUCCEEDED(block.layout->HitTestTextPosition(std::min(anchor->position,static_cast<UINT32>(block.text.size())),FALSE,&x,&y,&hit)))
                scroll_.restore({row->id,block.y+y+anchor->lineOffset});
        }
    }
    pendingAnchor_.reset();
    width_ = width; viewportHeight_ = height; changed_ = newGeneration_ = false;
}
ID2D1SolidColorBrush* ChatView::brush(ID2D1RenderTarget* target, std::uint32_t color) {
    auto& result = brushes_[color]; if (!result) require(target->CreateSolidColorBrush(D2D1::ColorF(color),&result),"Chat brush"); return result.Get();
}
void ChatView::paintBlock(ID2D1RenderTarget* target, ChatBlock& block, float x, float y) {
    if (outlineEnabled_ && (block.author || block.body)) {
        if (!block.outlineBuilt) {
            ComPtr<ID2D1Factory> geometryFactory; target->GetFactory(&geometryFactory);
            block.outline = buildTextOutline(factory_.Get(),geometryFactory.Get(),block.layout.Get());
            block.outlineBuilt = true; ++outlineBuilds_;
            if (!outlineStroke_) {
                auto properties = D2D1::StrokeStyleProperties(); properties.lineJoin = D2D1_LINE_JOIN_ROUND;
                require(geometryFactory->CreateStrokeStyle(properties,nullptr,0,&outlineStroke_),"Chat round outline stroke");
            }
        }
        D2D1_MATRIX_3X2_F original; target->GetTransform(&original);
        for (const auto& outline : block.outline) {
            target->SetTransform(D2D1::Matrix3x2F::Translation(x+outline.origin.x,y+outline.origin.y)*original);
            // Same convention as Full: 1 DIP outward thickness = 2 DIP centered stroke.
            target->DrawGeometry(outline.geometry.Get(),brush(target,0x000000),2,outlineStroke_.Get());
        }
        target->SetTransform(original);
    }
    for (const auto& span : block.spans) require(block.layout->SetDrawingEffect(brush(target,span.color),span.range),"Chat semantic color");
    target->DrawTextLayout(D2D1::Point2F(x,y),block.layout.Get(),brush(target,block.color),D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
}
void ChatView::draw(ID2D1RenderTarget* target, D2D1_RECT_F viewport) {
    viewport_ = viewport;
    arrange(viewport.right-viewport.left-18,viewport.bottom-viewport.top);
    target->PushAxisAlignedClip(viewport,D2D1_ANTIALIAS_MODE_ALIASED);
    float top = viewport.top-scroll_.offset();
    for (auto& row : rows_) {
        if (top+row.height > viewport.top && top < viewport.bottom) {
            if (row.attention == "ReplyToSelf") target->FillRectangle(D2D1::RectF(viewport.left,top,viewport.left+2,top+row.height),brush(target,0x58a6ff));
            for (auto& block : row.blocks) {
                if (block.body && mentionBackground_ && row.attention == "DirectSelfMention") target->FillRoundedRectangle(D2D1::RoundedRect(
                    D2D1::RectF(viewport.left,top+block.y,viewport.right-14,top+block.y+block.height),4,4),brush(target,0x23364b));
                paintBlock(target,block,viewport.left+4,top+block.y);
            }
            if (row.timeLayout) target->DrawTextLayout(D2D1::Point2F(viewport.right-68,top+18),row.timeLayout.Get(),brush(target,0x8b949e),D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
            if (row.iconLayout) target->DrawTextLayout(D2D1::Point2F(viewport.right-94,top+18),row.iconLayout.Get(),brush(target,row.blocks.front().color),D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
        }
        top += row.height;
    }
    if (scroll_.maximum() > 0) {
        auto* track = brush(target,0x57606a); track->SetOpacity(0.08f);
        target->FillRectangle(D2D1::RectF(viewport.right-10,viewport.top,viewport.right,viewport.bottom),track);
        track->SetOpacity(1);
        const float length = viewport.bottom-viewport.top, thumb = std::max(24.0f,length*length/scroll_.totalHeight());
        const float y = viewport.top+(length-thumb)*scroll_.offset()/scroll_.maximum();
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(viewport.right-5,y,viewport.right,y+thumb),2.5f,2.5f),brush(target,0x57606a));
    }
    target->PopAxisAlignedClip();
}
bool ChatView::pressScrollbar(float x, float y) {
    if (scroll_.maximum() <= 0 || x < viewport_.right-10 || x > viewport_.right ||
        y < viewport_.top || y > viewport_.bottom) return false;
    const float length = viewport_.bottom-viewport_.top;
    const float thumb = std::max(24.0f,length*length/scroll_.totalHeight());
    const float top = viewport_.top+(length-thumb)*scroll_.offset()/scroll_.maximum();
    scrollbarGrab_ = y >= top && y <= top+thumb ? y-top : thumb/2;
    draggingScrollbar_ = true; dragScrollbar(y); return true;
}
void ChatView::dragScrollbar(float y) {
    if (!draggingScrollbar_ || !std::isfinite(y)) return;
    const float length = viewport_.bottom-viewport_.top;
    const float thumb = std::max(24.0f,length*length/scroll_.totalHeight());
    if (length <= thumb) return;
    const float position = std::clamp((y-viewport_.top-scrollbarGrab_)/(length-thumb),0.0f,1.0f)*scroll_.maximum();
    scroll_.scroll(position-scroll_.offset());
}
void ChatView::releaseTargetResources() noexcept {
    for (auto& row : rows_) for (auto& block : row.blocks) if (block.layout)
        (void)block.layout->SetDrawingEffect(nullptr,{0,static_cast<UINT32>(block.text.size())});
    brushes_.clear();
}
void ChatView::cycleFont() {
    pendingAnchor_ = captureReadingAnchor();
    preset_ = (preset_+1)%static_cast<unsigned>(std::size(fontPairs));
    for (auto& row : rows_) { for (auto& block : row.blocks) block.layout.Reset(); row.timeLayout.Reset(); row.iconLayout.Reset(); }
    formats_.clear(); changed_ = true;
}
void ChatView::cycleRolePosition() {
    pendingAnchor_ = captureReadingAnchor();
    rolePosition_ = (rolePosition_+1)%3;
    std::vector<ChatRow> next;
    std::size_t index = 0,count = 0; yyjson_val* value = nullptr;
    yyjson_arr_foreach(field(snapshot_->root(),"chat"),index,count,value) next.push_back(makeRow(value));
    releaseTargetResources(); rows_ = std::move(next); changed_ = true;
}
void ChatView::changeSize(float delta) {
    pendingAnchor_ = captureReadingAnchor();
    const float next = std::clamp(fontSize_+delta,8.0f*96/72,32.0f*96/72);
    const float ratio = next/fontSize_; fontSize_ = next;
    for (auto& row : rows_) { for (auto& block : row.blocks) { block.size *= ratio; block.layout.Reset(); } row.timeLayout.Reset(); row.iconLayout.Reset(); }
    formats_.clear(); changed_ = true;
}
std::size_t ChatView::layoutTextBytes() const {
    std::size_t bytes = 0;
    for (const auto& row : rows_) for (const auto& block : row.blocks) bytes += block.text.capacity()*sizeof(wchar_t)+block.spans.capacity()*sizeof(ChatSpan);
    return bytes; // Explicit owned text/span estimate, NOT opaque DirectWrite allocation size.
}
std::wstring ChatView::fontName() const { return fontPairs[preset_].name; }
}
