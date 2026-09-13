#include "sales_view.hpp"
#include "transport.hpp"
#include "renderer.hpp"

namespace core {
namespace {
std::wstring optional(yyjson_val* value,const char* key) { auto* item = field(value,key); return yyjson_is_str(item) ? widen(stringValue(item)) : std::wstring{}; }
ChatBlock label(std::wstring text,float size,bool bold = false) { ChatBlock block; block.text = std::move(text); block.size = size; block.author = bold; return block; }
bool inside(D2D1_RECT_F bounds,float x,float y) { return x >= bounds.left && x <= bounds.right && y >= bounds.top && y <= bounds.bottom; }
}
void SalesView::setSnapshot(std::shared_ptr<const Json> snapshot, ChatView& text) {
    snapshot_ = std::move(snapshot);
    refreshSession(); auto* sales = field(snapshot_->root(),"sales"); auto* presentation = field(sales,"presentation");
    char* encoded = sales ? yyjson_val_write(sales,0,nullptr) : nullptr;
    std::string fingerprint = encoded ? encoded : "null"; free(encoded);
    fingerprint += "|"+std::string(textField(snapshot_->root(),"generation"))+"|"+narrow(optional(snapshot_->root(),"selfUserId"));
    if (fingerprint_ == fingerprint) return;
    fingerprint_ = std::move(fingerprint); rows_.clear(); width_ = 0;
    visible_ = yyjson_is_obj(presentation) && yyjson_is_true(field(presentation,"isVisible"));
    if (!visible_) { offset_ = 0; return; }
    primary_ = label(widen(textField(presentation,"primaryText")),13,true);
    secondary_ = label(optional(presentation,"secondaryText"),11); secondary_.color = 0x8b949e;
    const auto accent = textField(presentation,"accentKind");
    accentColor_ = accent == "CurrentTurn" ? 0x58a6ffU : accent == "NextTurn" ? 0x9e6bffU : 0x30363dU;
    toggle_ = label(expanded_ ? L"⌃" : L"⌄",16);
    auto* queue = field(sales,"queue");
    if (!yyjson_is_arr(queue) || yyjson_arr_size(queue) > 1000) throw std::runtime_error("Invalid canonical Sales queue");
    const auto current = optional(sales,"currentMessageId"), next = optional(sales,"nextMessageId");
    const auto viewer = optional(snapshot_->root(),"selfUserId");
    std::size_t index = 0,count = 0; yyjson_val* value = nullptr;
    yyjson_arr_foreach(queue,index,count,value) {
        Row row; row.id = std::string(textField(value,"messageId")); row.own = optional(value,"authorId") == viewer;
        std::wstring products; auto* items = field(value,"products");
        if (!yyjson_is_arr(items)) throw std::runtime_error("Invalid canonical product list");
        std::size_t i = 0,n = 0; yyjson_val* item = nullptr;
        yyjson_arr_foreach(items,i,n,item) {
            if (!products.empty()) products += L" · "; products += widen(textField(item,"name"));
            const auto quantity = numberField(item,"quantity"); if (quantity > 1) products += L" x"+std::to_wstring(quantity);
        }
        if (products.empty()) products = L"세부내용 확인";
        row.heading = label(std::to_wstring(index+1)+L"   "+widen(textField(value,"displayName"))+L" · "+products,12,true);
        row.detail = text.externalRuns(field(value,"detailRuns"),12);
        row.badge = label(std::wstring(widen(row.id) == current ? L"현재" : widen(row.id) == next ? L"다음" : L"")+(row.own ? L" · 나" : L""),11,true);
        row.badge.color = row.own ? 0x3fb950 : 0x8b949e;
        rows_.push_back(std::move(row));
    }
    readonly_ = label(L"개발 검증 · 판매 변경 없음",10); readonly_.color = 0x8b949e;
}
float SalesView::measure(ChatView& text,float width,float availableHeight) {
    if (!visible_) return 0;
    if (font_ != text.fontName()) { width_ = 0; session_.layout.Reset(); font_ = text.fontName(); }
    if (width_ != width) {
        text.layoutExternal(primary_,width-52);
        text.layoutExternal(secondary_,width-52); text.layoutExternal(toggle_,22); text.layoutExternal(readonly_,width-16);
        headerHeight_ = std::max(48.0f,primary_.height+16+(secondary_.text.empty() ? 0 : secondary_.height+2));
        totalHeight_ = 0;
        for (auto& row : rows_) {
            text.layoutExternal(row.heading,width-76); text.layoutExternal(row.detail,width-28); text.layoutExternal(row.badge,60);
            row.height = row.heading.height+12+(row.detail.text.empty() ? 0 : row.detail.height+4);
            row.y = totalHeight_; totalHeight_ += row.height+4;
        }
        width_ = width;
    }
    detailHeight_ = expanded_ && !rows_.empty() ? std::min(totalHeight_,std::max(0.0f,availableHeight-headerHeight_-readonly_.height-14)) : 0;
    offset_ = std::clamp(offset_,0.0f,std::max(0.0f,totalHeight_-detailHeight_));
    return headerHeight_+(detailHeight_ > 0 ? detailHeight_+readonly_.height+14 : 0);
}
void SalesView::draw(ID2D1RenderTarget* target,ChatView& text,D2D1_RECT_F panel) {
    panel_ = panel;
    if (!visible_) return;
    if (!surface_) require(target->CreateSolidColorBrush(D2D1::ColorF(0x161b22,0.96f),&surface_),"Sales surface");
    if (!accent_) require(target->CreateSolidColorBrush(D2D1::ColorF(accentColor_),&accent_),"Sales accent");
    accent_->SetColor(D2D1::ColorF(accentColor_));
    const auto header = D2D1::RectF(panel.left,panel.top,panel.right,panel.top+headerHeight_);
    target->FillRoundedRectangle(D2D1::RoundedRect(header,8,8),surface_.Get());
    target->DrawRoundedRectangle(D2D1::RoundedRect(header,8,8),accent_.Get(),1);
    const auto textHeight = primary_.height+(secondary_.text.empty() ? 0 : secondary_.height+2);
    const auto y = panel.top+(headerHeight_-textHeight)/2;
    text.drawExternal(target,primary_,panel.left+14,y,header);
    if (!secondary_.text.empty()) text.drawExternal(target,secondary_,panel.left+14,y+primary_.height+2,header);
    text.drawExternal(target,toggle_,panel.right-30,panel.top+(headerHeight_-toggle_.height)/2,header);
    details_ = D2D1::RectF(panel.left,panel.top+headerHeight_+6,panel.right,panel.top+headerHeight_+6+detailHeight_);
    if (detailHeight_ <= 0) return;
    target->PushAxisAlignedClip(details_,D2D1_ANTIALIAS_MODE_ALIASED);
    for (auto& row : rows_) {
        const auto top = details_.top+row.y-offset_;
        if (top >= details_.bottom || top+row.height <= details_.top) continue;
        const auto rect = D2D1::RectF(details_.left,top,details_.right,top+row.height);
        target->FillRoundedRectangle(D2D1::RoundedRect(rect,5,5),surface_.Get());
        text.drawExternal(target,row.heading,rect.left+8,top+5,details_);
        text.drawExternal(target,row.badge,rect.right-64,top+5,details_);
        if (!row.detail.text.empty()) text.drawExternal(target,row.detail,rect.left+14,top+row.heading.height+8,details_);
    }
    target->PopAxisAlignedClip();
    text.drawExternal(target,readonly_,panel.left+8,details_.bottom+3,panel);
}
void SalesView::refreshSession() {
    auto old = std::move(session_);
    session_ = label(L"세션 정보 확인 중",11); session_.color = 0x8b949e;
    if (!snapshot_) return;
    auto* sessions = field(snapshot_->root(),"session"); if (!yyjson_is_arr(sessions)) { if (old.text == session_.text) session_ = std::move(old); return; }
    std::size_t index = 0,count = 0; yyjson_val* host = nullptr;
    yyjson_arr_foreach(sessions,index,count,host) {
        if (numberField(host,"hostSlot") != hostSlot_) continue;
        const auto state = textField(host,"state");
        if (state == "offline") session_.text = L"호스트 오프라인";
        else if (state == "onlineButNotGtaOnline") session_.text = L"GTA Online 세션 아님";
        else if (state == "gtaOnline") {
            auto* current = field(host,"currentPlayers"), *maximum = field(host,"maximumPlayers");
            if (yyjson_is_int(current) && yyjson_is_int(maximum) && yyjson_get_sint(current) >= 0 && yyjson_get_sint(current) <= yyjson_get_sint(maximum) && yyjson_get_sint(maximum) <= 1000)
                session_.text = std::to_wstring(yyjson_get_sint(current))+L" / "+std::to_wstring(yyjson_get_sint(maximum));
            else session_.text = L"GTA Online · 인원 미확인";
        }
        break;
    }
    if (old.text == session_.text) session_ = std::move(old);
}
void SalesView::drawSession(ID2D1RenderTarget* target,ChatView& text,D2D1_RECT_F bounds) {
    if (font_ != text.fontName()) { width_ = 0; session_.layout.Reset(); font_ = text.fontName(); }
    if (!session_.layout) text.layoutExternal(session_,bounds.right-bounds.left);
    text.drawExternal(target,session_,bounds.left,bounds.top,bounds);
}
void SalesView::cycleHost() {
    if (!snapshot_) return;
    auto* sessions = field(snapshot_->root(),"session"); if (!yyjson_is_arr(sessions) || yyjson_arr_size(sessions) == 0) return;
    std::vector<unsigned> slots; std::size_t index = 0,count = 0; yyjson_val* host = nullptr;
    yyjson_arr_foreach(sessions,index,count,host) { const auto slot = numberField(host,"hostSlot"); if (slot > 0 && slot <= 100) slots.push_back(static_cast<unsigned>(slot)); }
    const auto found = std::find(slots.begin(),slots.end(),hostSlot_);
    if (!slots.empty()) hostSlot_ = found == slots.end() || std::next(found) == slots.end() ? slots.front() : *std::next(found);
    refreshSession();
}
bool SalesView::mediaUpdated(ChatView& text) {
    for (const auto& row : rows_) if (text.failedMedia(row.detail)) {
        // Re-tokenize once into the readable original emoji name. Ordinary
        // animation updates keep all Sales layouts and the collapse/scroll state.
        fingerprint_.clear(); setSnapshot(snapshot_,text); return true;
    }
    return false;
}
bool SalesView::click(float x,float y,bool unlocked) {
    if (!visible_ || !unlocked || !inside({panel_.left,panel_.top,panel_.right,panel_.top+headerHeight_},x,y)) return false;
    expanded_ = !expanded_; toggle_ = label(expanded_ ? L"⌃" : L"⌄",16); width_ = 0; return true;
}
bool SalesView::scroll(float x,float y,float delta) {
    if (!visible_ || !expanded_ || !inside(details_,x,y)) return false;
    offset_ = std::clamp(offset_+delta,0.0f,std::max(0.0f,totalHeight_-detailHeight_)); return true;
}
void SalesView::releaseTargetResources() {
    surface_.Reset(); accent_.Reset();
    const auto clear = [](ChatBlock& block) { if (block.layout) (void)block.layout->SetDrawingEffect(nullptr,{0,static_cast<UINT32>(block.text.size())}); };
    clear(primary_); clear(secondary_); clear(toggle_); clear(session_); clear(readonly_);
    for (auto& row : rows_) { clear(row.heading); clear(row.detail); clear(row.badge); }
}
}
