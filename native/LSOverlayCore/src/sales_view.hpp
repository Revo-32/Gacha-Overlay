#pragma once
#include "chat_view.hpp"
#include "sales_actions.hpp"

namespace core {
// Consumes canonical semantic fields only. No SOLD/closed/Discord Sales parser.
class SalesView final {
public:
    void setSnapshot(std::shared_ptr<const Json> snapshot, ChatView& text);
    void applySettings(const UserSettings& settings,ChatView& text);
    float measure(ChatView& text, float width, float availableHeight);
    void draw(ID2D1RenderTarget* target, ChatView& text, D2D1_RECT_F panel, bool unlocked);
    void drawSession(ID2D1RenderTarget* target, ChatView& text, D2D1_RECT_F bounds);
    void releaseTargetResources();
    bool click(float x, float y, bool unlocked);
    void setAction(std::optional<SalesCommand> command,bool pending,std::wstring status);
    std::optional<SalesCommand> actionAt(float x,float y,bool unlocked) const;
    bool scroll(float x, float y, float delta);
    void cycleHost();
    bool mediaUpdated(ChatView& text);
    bool expanded() const { return expanded_; }
    bool visible() const { return visible_; }
    std::size_t count() const { return rows_.size(); }
    std::wstring sessionLabel() const { return session_.text; }
    std::wstring headline() const { return primary_.text; }
    D2D1_RECT_F headerBounds() const { return {panel_.left,panel_.top,panel_.right,panel_.top+headerHeight_}; }
    D2D1_RECT_F actionBounds() const { return actionBounds_; }
    D2D1_RECT_F actionTextBounds() const { return actionTextBounds_; }
    D2D1_RECT_F headlineTextBounds() const { return headlineTextBounds_; }
    void invalidateLayout() { width_ = 0; }
private:
    struct Row { std::string id; ChatBlock heading, detail, badge; float y = 0,height = 0; bool own = false; };
    std::shared_ptr<const Json> snapshot_;
    std::vector<Row> rows_;
    ChatBlock primary_,secondary_,toggle_,session_,readonly_,actionLabel_,actionStatus_;
    std::optional<SalesCommand> action_;
    bool actionPending_=false;
    D2D1_RECT_F actionBounds_{};
    D2D1_RECT_F actionTextBounds_{};
    DWRITE_TEXT_METRICS actionMetrics_{};
    DWRITE_TEXT_METRICS primaryMetrics_{},secondaryMetrics_{};
    D2D1_RECT_F headlineTextBounds_{};
    bool controlsShown_=false;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> surface_,accent_;
    float width_ = 0, headerHeight_ = 48, detailHeight_ = 0, totalHeight_ = 0, offset_ = 0;
    bool expanded_ = true,visible_ = false;
    unsigned hostSlot_ = 1;
    std::uint32_t accentColor_ = 0x30363d;
    std::string fingerprint_;
    std::wstring font_;
    std::uint64_t mediaRevision_=0;
    UserSettings settings_;bool settingsActive_=false;
    D2D1_RECT_F panel_{},details_{};
    void refreshSession();
};
}
