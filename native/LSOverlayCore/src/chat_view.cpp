#include "chat_view.hpp"
#include "renderer.hpp"
#include "transport.hpp"
#include "media_layout.hpp"
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
// DirectWrite reserves the real emoji cell in wrapping/hit testing. Its pixels
// are painted separately through the shared visible-media renderer.
class InlineMedia final : public IDWriteInlineObject {
public:
    explicit InlineMedia(float size) : size_(size) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER; *result = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IDWriteInlineObject)) return E_NOINTERFACE;
        *result = static_cast<IDWriteInlineObject*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override { const auto refs = --refs_; if (!refs) delete this; return refs; }
    HRESULT STDMETHODCALLTYPE Draw(void*,IDWriteTextRenderer*,FLOAT,FLOAT,BOOL,BOOL,IUnknown*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE GetMetrics(DWRITE_INLINE_OBJECT_METRICS* result) override {
        if (!result) return E_POINTER; *result = {size_,size_,size_*0.82f,FALSE}; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetOverhangMetrics(DWRITE_OVERHANG_METRICS* result) override { if (!result) return E_POINTER; *result = {}; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetBreakConditions(DWRITE_BREAK_CONDITION* before,DWRITE_BREAK_CONDITION* after) override {
        if (!before || !after) return E_POINTER; *before = *after = DWRITE_BREAK_CONDITION_NEUTRAL; return S_OK;
    }
private:
    std::atomic<ULONG> refs_{1}; float size_;
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
ChatBlock ChatView::runs(yyjson_val* values, float size, std::wstring prefix, bool chatEmoji) const {
    if (!yyjson_is_arr(values)) throw std::runtime_error("Invalid Core runs");
    ChatBlock block; block.size = size; block.text = std::move(prefix);
    std::size_t index = 0,count = 0; yyjson_val* run = nullptr;
    yyjson_arr_foreach(values,index,count,run) {
        const auto kind = textField(run,"kind"); auto text = widen(textField(run,"text"));
        const auto mediaId = field(run,"mediaId");
        // Only a server-associated primary preview URL may disappear. Keep the
        // original text before first successful paint readiness and on failure.
        if (kind == "MediaSource" && (!settingsActive_ || (settings_.enabled(Setting::HideUrl) && settings_.enabled(Setting::Images))) && media_ && yyjson_is_str(mediaId) && media_->ready(std::string(stringValue(mediaId)))) continue;
        const float emojiSize=settingsActive_ && chatEmoji?settings_.get(Setting::EmojiSize):size;
        const auto physical=static_cast<unsigned>(std::ceil(emojiSize*mediaDpi_/96*1.25f));
        if (kind == "CustomEmoji" && (!settingsActive_ || settings_.enabled(Setting::Emoji)) && media_ && yyjson_is_str(mediaId) && media_->prepare(std::string(stringValue(mediaId)),physical,physical)) {
            block.images.push_back({static_cast<UINT32>(block.text.size()),std::string(stringValue(mediaId)),emojiSize});
            block.text += L'\xfffc'; continue;
        }
        // Preserve the name on cache miss, unsupported asset or failed decode.
        if (kind == "CustomEmoji" && !text.empty() && text.front() != L':') text = L":"+text+L":";
        const auto start = static_cast<UINT32>(block.text.size()); block.text += text;
        if (kind == "Mention") block.spans.push_back({{start,static_cast<UINT32>(text.size())},
            yyjson_is_true(field(run,"isSelf")) ? 0x3fb950U : 0xc9d1d9U,yyjson_is_true(field(run,"isSelf"))});
    }
    if (std::all_of(block.text.begin(),block.text.end(),[](wchar_t c) {return iswspace(c)!=0;})) block.text.clear();
    return block;
}
ChatBlock ChatView::mediaBlock(yyjson_val* asset, bool forwarded) const {
    ChatBlock block; block.size = fontSize_*0.85f; block.color = 0x8b949e;
    const std::string id(textField(asset,"id"));
    if (media_ && media_->prepare(id,1,1)) block.mediaId = id;
    auto* w=field(asset,"width");auto* h=field(asset,"height");
    if (yyjson_is_uint(w) && yyjson_is_uint(h) && yyjson_get_uint(w)>0 && yyjson_get_uint(h)>0 && yyjson_get_uint(w)<=32768 && yyjson_get_uint(h)<=32768) {
        block.sourceWidth=static_cast<unsigned>(yyjson_get_uint(w));block.sourceHeight=static_cast<unsigned>(yyjson_get_uint(h));
    }
    block.text = (forwarded ? L"[전달된 미디어: " : L"[미디어: ")+optionalText(asset,"name")+L" · "+
        (media_ && media_->failed(id) ? L"불러올 수 없음]" : L"미리보기 없음]");
    return block;
}
ChatRow ChatView::makeRow(yyjson_val* value) const {
    ChatRow row; row.id = textField(value,"id");
    row.fingerprint = std::string(textField(value,"presentationHash"));
    row.attention = textField(value,"attention"); row.header = yyjson_is_true(field(value,"showAuthorHeader"));
    auto* author = field(value,"author");
    if (row.header) {
        ChatBlock header; header.size = fontSize_; header.author = true;
        auto icon = optionalText(author,"iconUnicode");
        const auto image = optionalText(author,"iconMediaId");
        const auto physical=static_cast<unsigned>(std::ceil(fontSize_*mediaDpi_/96*1.25f));
        const bool imageReady = media_ && !image.empty() && media_->prepare(narrow(image),physical,physical);
        if (imageReady) icon = L"\xfffc";
        const auto name = widen(textField(author,"displayName"));
        header.text = rolePosition_ == 0 ? (icon.empty() ? L"" : icon+L" ")+name :
            rolePosition_ == 1 ? name+(icon.empty() ? L"" : L" "+icon) : name;
        if (rolePosition_ == 2) row.icon = icon;
        if (imageReady) {
            if (rolePosition_ == 2) { row.icon.clear(); row.iconMediaId = narrow(image); }
            else header.images.push_back({rolePosition_ == 0 ? 0U : static_cast<UINT32>(name.size()+1),narrow(image),fontSize_});
        }
        auto* color = field(author,"color"); if (yyjson_is_uint(color) && yyjson_get_uint(color) <= 0xffffff) header.color = static_cast<std::uint32_t>(yyjson_get_uint(color));
        row.blocks.push_back(std::move(header)); if(!settingsActive_ || settings_.enabled(Setting::ShowTime))row.time = localTime(value);
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
            if(settingsActive_ && !settings_.enabled(textField(asset,"kind")=="sticker"?Setting::Stickers:Setting::Images))continue;
            if (assetIndex == 0) row.blocks.push_back(mediaBlock(asset,true));
            else if (assetIndex == 1) { ChatBlock extra; extra.size = fontSize_*0.85f; extra.text = L"+"+std::to_wstring(assetCount-1)+L"개 미디어"; row.blocks.push_back(std::move(extra)); }
        }
    }
    auto* media = field(value,"media");
    if (!yyjson_is_arr(media)) throw std::runtime_error("Invalid Core media metadata");
    yyjson_val* item = nullptr;
    yyjson_arr_foreach(media,index,count,item) {
        if(settingsActive_ && !settings_.enabled(textField(item,"kind")=="sticker"?Setting::Stickers:Setting::Images))continue;
        if (index == 0) row.blocks.push_back(mediaBlock(item,false));
        else if (index == 1) { ChatBlock extra; extra.size = fontSize_*0.85f; extra.text = L"+"+std::to_wstring(count-1)+L"개 미디어"; row.blocks.push_back(std::move(extra)); }
    }
    auto* reactions = field(value,"reactions");
    if (!yyjson_is_arr(reactions)) throw std::runtime_error("Invalid Core reactions");
    ChatBlock reactionBlock; reactionBlock.size = settingsActive_?settings_.get(Setting::ReactionSize):fontSize_*0.85f; reactionBlock.color = 0xc9d1d9;
    yyjson_arr_foreach(reactions,index,count,item) {
        auto* emoji = field(item,"emoji");
        const auto text = widen(textField(emoji,"text"));
        auto* mediaId = field(emoji,"mediaId");
        const auto physical=static_cast<unsigned>(std::ceil(reactionBlock.size*mediaDpi_/96*1.25f));
        if ((!settingsActive_ || settings_.enabled(Setting::Emoji)) && media_ && yyjson_is_str(mediaId) && media_->prepare(std::string(stringValue(mediaId)),physical,physical)) {
            reactionBlock.images.push_back({static_cast<UINT32>(reactionBlock.text.size()),std::string(stringValue(mediaId)),reactionBlock.size});
            reactionBlock.text += L'\xfffc';
        } else reactionBlock.text += textField(emoji,"kind") == "CustomEmoji" ? L":"+text+L":" : text;
        reactionBlock.text += L" "+std::to_wstring(numberField(item,"count"))+L"   ";
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
    if (!block.mediaId.empty() && media_) {
        const bool large=!settingsActive_ || settings_.enabled(Setting::LargeImages);
        auto physical=static_cast<unsigned>(std::ceil(std::max(1.0f,std::min(width,large?360.0f:132.0f))*mediaDpi_/96*1.25f));
        if (block.sourceWidth) physical=std::min(physical,block.sourceWidth);
        auto height=block.sourceWidth ? static_cast<unsigned>(std::ceil(static_cast<double>(physical)*block.sourceHeight/block.sourceWidth)) : std::min(8192U,16777216U/std::max(physical,1U));
        // Unsupported dimensions fall back explicitly; never quietly downscale
        // below the physical requirement to satisfy the safety admission limit.
        if (physical<=8192 && height<=8192 && static_cast<std::uint64_t>(physical)*height<=16777216) media_->prepare(block.mediaId,physical,std::max(height,1U));
        else block.mediaId.clear();
        if (const auto size = media_->dimensions(block.mediaId)) {
            // Before delivery use canonical aspect, or a modest placeholder for
            // metadata-free stickers; never reserve the conversion safety bound.
            const bool pending=!media_->frame(block.mediaId);
            const auto ratio=pending && block.sourceWidth ? static_cast<float>(block.sourceHeight)/static_cast<float>(block.sourceWidth) :
                pending && !block.sourceWidth && size->height==8192 ? 1.0f : static_cast<float>(size->height)/static_cast<float>(size->width);
            const auto box=large?chatMediaBox(width,1.0f,ratio):fitMedia(std::min(width,132.0f),96,1,ratio);
            block.imageWidth=box.width;block.height=box.height; return;
        }
        block.mediaId.clear();
    }
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
    ComPtr<IDWriteFontFamily> fontFamily;ComPtr<IDWriteFont> font;DWRITE_FONT_METRICS fontMetrics{};
    require(fonts_->GetFontFamily(index,&fontFamily),"Chat font family metrics");
    require(fontFamily->GetFirstMatchingFont(block.author?pair.authorWeight:pair.bodyWeight,DWRITE_FONT_STRETCH_NORMAL,DWRITE_FONT_STYLE_NORMAL,&font),"Chat font metrics");
    font->GetMetrics(&fontMetrics);
    const float em=block.size/static_cast<float>(fontMetrics.designUnitsPerEm);
    const float natural=em*(fontMetrics.ascent+fontMetrics.descent);
    const float line=std::max(natural,std::ceil(block.size*(settingsActive_?settings_.get(Setting::LineHeight):1.42f)));
    require(format->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM,line,em*fontMetrics.ascent+(line-natural)/2),"Chat line spacing");
    }
    require(factory_->CreateTextLayout(block.text.c_str(),static_cast<UINT32>(block.text.size()),format.Get(),std::max(width,1.0f),1e7f,&block.layout),"Chat layout");
    for (const auto& span : block.spans) if (span.bold) require(block.layout->SetFontWeight(DWRITE_FONT_WEIGHT_BOLD,span.range),"Mention weight");
    for (const auto& image : block.images) {
        if (media_) {const auto physical=static_cast<unsigned>(std::ceil(image.size*mediaDpi_/96*1.25f));media_->prepare(image.id,physical,physical);}
        ComPtr<IDWriteInlineObject> object; object.Attach(new InlineMedia(image.size));
        require(block.layout->SetInlineObject(object.Get(),{image.position,1}),"Inline media layout");
    }
    DWRITE_LINE_SPACING_METHOD method{};float line=0,baseline=0;require(format->GetLineSpacing(&method,&line,&baseline),"Chat line metrics");
    float above=baseline,below=line-baseline;
    for(const auto& image:block.images){above=std::max(above,image.size*0.82f);below=std::max(below,image.size*0.18f);}
    line=above+below;
    require(block.layout->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM,line,above),"Emoji-safe line spacing");
    if(settingsActive_ && block.body) {
        ComPtr<IDWriteInlineObject> ellipsis;require(factory_->CreateEllipsisTrimmingSign(format.Get(),&ellipsis),"Chat trimming sign");
        DWRITE_TRIMMING trim{DWRITE_TRIMMING_GRANULARITY_CHARACTER,0,0};require(block.layout->SetTrimming(&trim,ellipsis.Get()),"Chat line limit trimming");
        require(block.layout->SetMaxHeight(line*settings_.get(Setting::MaxLines)),"Chat line limit height");
    }
    DWRITE_TEXT_METRICS metrics{}; require(block.layout->GetMetrics(&metrics),"Chat layout measurement"); block.height = metrics.height;
    if(settingsActive_ && block.body)block.height=std::min(block.height,line*settings_.get(Setting::MaxLines));
    block.inkPadding=settingsActive_?(settings_.enabled(block.author?Setting::NicknameOutline:Setting::MessageOutline)?std::ceil(settings_.get(block.author?Setting::NicknameThickness:Setting::MessageThickness)):0)+std::min(1.0f,settings_.get(Setting::LineHeight)):2;
    block.height+=block.inkPadding*2;
    ++layoutBuilds_;
}
void ChatView::arrange(float width, float height) {
    if (!changed_ && width_ == width && viewportHeight_ == height) return;
    const auto anchor = pendingAnchor_ ? pendingAnchor_ : captureReadingAnchor();
    const bool reflow = width_ != width;
    std::vector<ChatExtent> extents; extents.reserve(rows_.size());
    for (auto& row : rows_) {
        float y = row.header ? (settingsActive_?std::max(0.0f,settings_.get(Setting::Spacing)):18.0f) : 2.0f;
        for (auto& block : row.blocks) {
            if (reflow || !block.layout) layout(block,width-(block.author ? (row.time.empty() ? 0 : 58)+(row.icon.empty() && row.iconMediaId.empty() ? 0 : 24) : 0));
            block.y = y; y += block.height+(settingsActive_?2*std::min(1.0f,settings_.get(Setting::LineHeight)):2);
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
            if (block.layout && SUCCEEDED(block.layout->HitTestTextPosition(std::min(anchor->position,static_cast<UINT32>(block.text.size())),FALSE,&x,&y,&hit)))
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
    if (!block.mediaId.empty() && media_) {
        const auto size = media_->dimensions(block.mediaId);
        if (size) paintMedia(target,block.mediaId,D2D1::RectF(x,y,x+block.imageWidth,y+block.height));
        return;
    }
    const bool outlineEnabled=block.outlineOverride>=0?block.outlineOverride>0:settingsActive_ ? (block.author?settings_.enabled(Setting::NicknameOutline):block.body && settings_.enabled(Setting::MessageOutline)) : outlineEnabled_ && (block.author || block.body);
    if (outlineEnabled) {
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
            target->DrawGeometry(outline.geometry.Get(),brush(target,0x000000),block.outlineOverride>=0?2*block.outlineOverride:settingsActive_?2*settings_.get(block.author?Setting::NicknameThickness:Setting::MessageThickness):2,outlineStroke_.Get());
        }
        target->SetTransform(original);
    }
    for (const auto& span : block.spans) require(block.layout->SetDrawingEffect(brush(target,span.color),span.range),"Chat semantic color");
    target->DrawTextLayout(D2D1::Point2F(x,y),block.layout.Get(),brush(target,block.color),D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
    for (const auto& image : block.images) {
        FLOAT left = 0,top = 0; DWRITE_HIT_TEST_METRICS hit{};
        if (SUCCEEDED(block.layout->HitTestTextPosition(image.position,FALSE,&left,&top,&hit)))
            paintMedia(target,image.id,D2D1::RectF(x+left,y+top,x+left+image.size,y+top+image.size));
    }
}
void ChatView::paintMedia(ID2D1RenderTarget* target, const std::string& id, D2D1_RECT_F bounds) {
    if (!media_ || bounds.bottom <= viewport_.top || bounds.top >= viewport_.bottom || bounds.right <= viewport_.left || bounds.left >= viewport_.right) return;
    visibleMedia_.insert(id);
    if (recordingMedia_) { mediaPlacements_.push_back({id,bounds,viewport_}); return; }
    const auto pixels = media_->frame(id);
    if (!pixels) {
        target->FillRoundedRectangle(D2D1::RoundedRect(bounds,4,4),brush(target,0x21262d));
        if (bounds.right-bounds.left>=160 && bounds.bottom-bounds.top>=40) {
            if (!loadingLayout_) {
                ChatBlock loading;loading.text=L"이미지 불러오는 중…";loading.size=12;
                layout(loading,150);loadingLayout_=std::move(loading.layout);
            }
            target->DrawTextLayout(D2D1::Point2F(bounds.left+8,bounds.top+8),loadingLayout_.Get(),brush(target,0x8b949e));
        }
        return;
    }
    const auto key = media_->sourceKey(id); visibleBitmaps_.insert(key);
    auto& cached = mediaBitmaps_[key];
    if (!cached.bitmap || cached.index != pixels->index) {
        const auto properties = D2D1::BitmapProperties(D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
        if (!cached.bitmap) require(target->CreateBitmap(D2D1::SizeU(pixels->width,pixels->height),pixels->bgra.data(),pixels->width*4,properties,&cached.bitmap),"Native media bitmap");
        else require(cached.bitmap->CopyFromMemory(nullptr,pixels->bgra.data(),pixels->width*4),"Next native media frame");
        cached.index = pixels->index;
    }
    const auto fitted=fitMedia(bounds.right-bounds.left,bounds.bottom-bounds.top,static_cast<float>(pixels->width),static_cast<float>(pixels->height));
    const auto left=bounds.left+(bounds.right-bounds.left-fitted.width)/2;
    const auto top=bounds.top+(bounds.bottom-bounds.top-fitted.height)/2;
    target->DrawBitmap(cached.bitmap.Get(),D2D1::RectF(left,top,left+fitted.width,top+fitted.height),1,D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
}
bool ChatView::mediaUpdated() {
    if (!media_) return false;
    media_->acknowledge();
    // Failed assets revert to their original readable names, including inline
    // emoji. Rebuild only once per failure change; ordinary frames keep layouts.
    const auto revision=media_->layoutRevision();bool rebuild = revision!=mediaRevision_;mediaRevision_=revision;
    for (const auto& row : rows_) {
        if (!row.iconMediaId.empty() && media_->failed(row.iconMediaId)) rebuild = true;
        for (const auto& block : row.blocks) {
            if (!block.mediaId.empty() && media_->failed(block.mediaId)) rebuild = true;
            for (const auto& image : block.images) if (media_->failed(image.id)) rebuild = true;
        }
    }
    if (rebuild && snapshot_) {
        pendingAnchor_ = captureReadingAnchor(); std::vector<ChatRow> next;
        std::size_t index = 0,count = 0; yyjson_val* value = nullptr;
        yyjson_arr_foreach(field(snapshot_->root(),"chat"),index,count,value) next.push_back(makeRow(value));
        releaseTargetResources(); rows_ = std::move(next); changed_ = true;
    }
    return rebuild;
}
void ChatView::pauseMedia() { if (media_) media_->setVisible({}); mediaBitmaps_.clear(); }
void ChatView::drawMedia(ID2D1RenderTarget* target) {
    visibleBitmaps_.clear(); const auto original = viewport_;
    for (const auto& placement : mediaPlacements_) {
        viewport_ = placement.clip; target->PushAxisAlignedClip(viewport_,D2D1_ANTIALIAS_MODE_ALIASED);
        paintMedia(target,placement.id,placement.bounds); target->PopAxisAlignedClip();
    }
    viewport_ = original;
    if(!enlargedId_.empty() && media_) {
        const auto bounds=D2D1::RectF(viewport_.left+8,viewport_.top+36,viewport_.right-8,viewport_.bottom-8);
        target->FillRoundedRectangle(D2D1::RoundedRect(viewport_,8,8),brush(target,0x161b22));
        if(!enlargementLayout_) {ChatBlock close;close.text=L"확대 미리보기 · 클릭하면 닫기";close.size=12;layout(close,300);enlargementLayout_=std::move(close.layout);}
        target->DrawTextLayout(D2D1::Point2F(viewport_.left+8,viewport_.top+8),enlargementLayout_.Get(),brush(target,0xc9d1d9));
        if(const auto size=media_->dimensions(enlargedId_)) {
            const auto fitted=fitMedia(bounds.right-bounds.left,bounds.bottom-bounds.top,static_cast<float>(size->width),static_cast<float>(size->height));
            const auto width=static_cast<unsigned>(std::ceil(std::max(1.0f,fitted.width)*mediaDpi_/96*1.25f)),height=static_cast<unsigned>(std::ceil(std::max(1.0f,fitted.height)*mediaDpi_/96*1.25f));
            if(width<=8192 && height<=8192 && static_cast<std::uint64_t>(width)*height<=16777216)media_->prepare(enlargedId_,width,height);
            paintMedia(target,enlargedId_,bounds);
        }else enlargedId_.clear();
    }
    std::erase_if(mediaBitmaps_,[&](const auto& pair) { return !visibleBitmaps_.contains(pair.first); });
    commitMediaVisibility();
}
bool ChatView::clickMedia(float x,float y) {
    if(!enlargedId_.empty()) {enlargedId_.clear();return true;}
    if(!settingsActive_ || !settings_.enabled(Setting::Enlarge))return false;
    for(const auto& placement:mediaPlacements_)if(placement.bounds.right-placement.bounds.left>48 && placement.bounds.bottom-placement.bounds.top>48 &&
        x>=placement.bounds.left && x<placement.bounds.right && y>=placement.bounds.top && y<placement.bounds.bottom && y>=placement.clip.top && y<placement.clip.bottom) {enlargedId_=placement.id;return true;}
    return false;
}
void ChatView::draw(ID2D1RenderTarget* target, D2D1_RECT_F viewport, bool showScrollbar) {
    scrollbarVisible_=showScrollbar;if(!showScrollbar)draggingScrollbar_=false;
    viewport_ = viewport;
    float dpiX = 0,dpiY = 0; target->GetDpi(&dpiX,&dpiY);
    if (mediaDpi_ != dpiX) { mediaDpi_ = dpiX; width_ = 0; }
    visibleMedia_.clear();
    visibleBitmaps_.clear();
    mediaPlacements_.clear(); recordingMedia_ = true;
    arrange(viewport.right-viewport.left-18,viewport.bottom-viewport.top);
    target->PushAxisAlignedClip(viewport,D2D1_ANTIALIAS_MODE_ALIASED);
    float top = viewport.top-scroll_.offset();
    for (auto& row : rows_) {
        if (top+row.height > viewport.top && top < viewport.bottom) {
            if (row.attention == "ReplyToSelf") target->FillRectangle(D2D1::RectF(viewport.left,top,viewport.left+2,top+row.height),brush(target,0x58a6ff));
            for (auto& block : row.blocks) {
                if (block.body && mentionBackground_ && row.attention == "DirectSelfMention") target->FillRoundedRectangle(D2D1::RoundedRect(
                    D2D1::RectF(viewport.left,top+block.y,viewport.right-14,top+block.y+block.height),4,4),brush(target,0x23364b));
                target->PushAxisAlignedClip(D2D1::RectF(viewport.left,top+block.y,viewport.right,top+block.y+block.height),D2D1_ANTIALIAS_MODE_ALIASED);
                const auto fullClip=viewport_;
                viewport_.top=std::max(viewport.top,top+block.y);viewport_.bottom=std::min(viewport.bottom,top+block.y+block.height);
                paintBlock(target,block,viewport.left+4,top+block.y+block.inkPadding);viewport_=fullClip;target->PopAxisAlignedClip();
            }
            const float headerY=top+(settingsActive_?std::max(0.0f,settings_.get(Setting::Spacing)):18);
            if (row.timeLayout) target->DrawTextLayout(D2D1::Point2F(viewport.right-68,headerY),row.timeLayout.Get(),brush(target,0x8b949e),D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
            if (row.iconLayout) target->DrawTextLayout(D2D1::Point2F(viewport.right-94,headerY),row.iconLayout.Get(),brush(target,row.blocks.front().color),D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
            if (!row.iconMediaId.empty()) paintMedia(target,row.iconMediaId,D2D1::RectF(viewport.right-94,headerY,viewport.right-94+fontSize_,headerY+fontSize_));
        }
        top += row.height;
    }
    if (scrollbarVisible()) {
        auto* track = brush(target,0x57606a); track->SetOpacity(0.08f);
        target->FillRectangle(D2D1::RectF(viewport.right-10,viewport.top,viewport.right,viewport.bottom),track);
        track->SetOpacity(1);
        const float length = viewport.bottom-viewport.top, thumb = std::max(24.0f,length*length/scroll_.totalHeight());
        const float y = viewport.top+(length-thumb)*scroll_.offset()/scroll_.maximum();
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(viewport.right-5,y,viewport.right,y+thumb),2.5f,2.5f),brush(target,0x57606a));
    }
    target->PopAxisAlignedClip();
    recordingMedia_ = false;
}
void ChatView::drawExternal(ID2D1RenderTarget* target, ChatBlock& block, float x, float y, D2D1_RECT_F clip) {
    const auto original = viewport_; viewport_ = clip; recordingMedia_ = true;
    paintBlock(target,block,x,y); recordingMedia_ = false; viewport_ = original;
}
bool ChatView::pressScrollbar(float x, float y) {
    if (!scrollbarVisible() || x < viewport_.right-10 || x > viewport_.right ||
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
    mediaBitmaps_.clear();
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
    for (auto& row : rows_) { for (auto& block : row.blocks) { block.size *= ratio; for (auto& image : block.images) image.size *= ratio; block.layout.Reset(); } row.timeLayout.Reset(); row.iconLayout.Reset(); }
    formats_.clear(); changed_ = true;
}
std::size_t ChatView::layoutTextBytes() const {
    std::size_t bytes = 0;
    for (const auto& row : rows_) for (const auto& block : row.blocks) bytes += block.text.capacity()*sizeof(wchar_t)+block.spans.capacity()*sizeof(ChatSpan);
    return bytes; // Explicit owned text/span estimate, NOT opaque DirectWrite allocation size.
}
std::wstring ChatView::fontName() const { return fontPairs[preset_].name; }
void ChatView::applySettings(const UserSettings& settings) {
    if(settingsActive_ && settings_==settings)return;
    pendingAnchor_=captureReadingAnchor();settings_=settings;settingsActive_=true;
    preset_=static_cast<unsigned>(settings.get(Setting::Font));fontSize_=settings.get(Setting::FontSize)*96/72;
    rolePosition_=static_cast<unsigned>(settings.get(Setting::RolePosition));mentionBackground_=settings.enabled(Setting::MentionBackground);
    if(!settings.enabled(Setting::Images) || !settings.enabled(Setting::Enlarge))enlargedId_.clear();
    formats_.clear();loadingLayout_.Reset();enlargementLayout_.Reset();releaseTargetResources();width_=0;changed_=true;
    if(snapshot_) {std::vector<ChatRow> rows;std::size_t i=0,n=0;yyjson_val* value=nullptr;yyjson_arr_foreach(field(snapshot_->root(),"chat"),i,n,value)rows.push_back(makeRow(value));rows_=std::move(rows);}
}
}
