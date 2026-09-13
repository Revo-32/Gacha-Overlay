#pragma once
#include "chat_scroll.hpp"
#include "json.hpp"
#include "text_outline.hpp"
#include <d2d1_1.h>
#include <dwrite_3.h>
#include <wrl/client.h>
#include <memory>
#include <unordered_map>
#include <optional>

namespace core {
struct ChatSpan { DWRITE_TEXT_RANGE range; std::uint32_t color; bool bold = false; };
struct ChatBlock {
    std::wstring text;
    std::vector<ChatSpan> spans;
    Microsoft::WRL::ComPtr<IDWriteTextLayout> layout;
    std::vector<GlyphOutline> outline;
    float y = 0, height = 0, size = 16;
    std::uint32_t color = 0xf0f6fc;
    bool author = false, body = false;
    bool outlineBuilt = false;
};
struct ChatRow {
    std::string id, fingerprint, attention;
    std::vector<ChatBlock> blocks;
    std::wstring time;
    std::wstring icon;
    Microsoft::WRL::ComPtr<IDWriteTextLayout> timeLayout;
    Microsoft::WRL::ComPtr<IDWriteTextLayout> iconLayout;
    float height = 0;
    bool header = false;
};
class ChatView final {
public:
    explicit ChatView(IDWriteFactory* factory);
    void setSnapshot(std::shared_ptr<const Json> snapshot);
    void draw(ID2D1RenderTarget* target, D2D1_RECT_F viewport);
    void scroll(float delta) { scroll_.scroll(delta); }
    void followLatest() { scroll_.followLatest(); }
    void cycleFont();
    void cycleRolePosition();
    void changeSize(float delta);
    void toggleMentionBackground() { mentionBackground_ = !mentionBackground_; }
    void toggleOutline() { outlineEnabled_ = !outlineEnabled_; }
    bool pressScrollbar(float x, float y);
    void dragScrollbar(float y);
    void endScrollbar() { draggingScrollbar_ = false; }
    bool draggingScrollbar() const { return draggingScrollbar_; }
    void releaseTargetResources() noexcept;
    bool active() const { return snapshot_ != nullptr; }
    std::size_t messageCount() const { return rows_.size(); }
    const ChatScroll& scrolling() const { return scroll_; }
    std::uint64_t layoutBuilds() const { return layoutBuilds_; }
    std::uint64_t outlineBuilds() const { return outlineBuilds_; }
    std::size_t layoutTextBytes() const;
    std::wstring fontName() const;
private:
    struct TextAnchor { std::string id, fingerprint; std::size_t block; UINT32 position; float lineOffset; };
    std::optional<TextAnchor> captureReadingAnchor() const;
    void arrange(float width, float height);
    ChatRow makeRow(yyjson_val* value) const;
    ChatBlock runs(yyjson_val* values, float size, std::wstring prefix = {}) const;
    void layout(ChatBlock& block, float width);
    void paintBlock(ID2D1RenderTarget* target, ChatBlock& block, float x, float y);
    ID2D1SolidColorBrush* brush(ID2D1RenderTarget* target, std::uint32_t color);
    Microsoft::WRL::ComPtr<IDWriteFactory> factory_;
    Microsoft::WRL::ComPtr<IDWriteFontCollection> fonts_;
    Microsoft::WRL::ComPtr<ID2D1StrokeStyle> outlineStroke_;
    std::unordered_map<std::uint64_t,Microsoft::WRL::ComPtr<IDWriteTextFormat>> formats_;
    std::unordered_map<std::uint32_t,Microsoft::WRL::ComPtr<ID2D1SolidColorBrush>> brushes_;
    std::shared_ptr<const Json> snapshot_;
    std::vector<ChatRow> rows_;
    ChatScroll scroll_;
    std::string generation_;
    float width_ = 0, viewportHeight_ = 0, fontSize_ = 16;
    unsigned preset_ = 0, rolePosition_ = 0;
    bool changed_ = false, newGeneration_ = true, mentionBackground_ = true;
    bool outlineEnabled_ = true, draggingScrollbar_ = false;
    float scrollbarGrab_ = 0;
    D2D1_RECT_F viewport_{};
    std::uint64_t layoutBuilds_ = 0;
    std::uint64_t outlineBuilds_ = 0;
    std::optional<TextAnchor> pendingAnchor_;
};
}
