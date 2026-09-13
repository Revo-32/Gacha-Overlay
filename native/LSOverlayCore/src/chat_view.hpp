#pragma once
#include "chat_scroll.hpp"
#include "json.hpp"
#include "text_outline.hpp"
#include "media_store.hpp"
#include "user_settings.hpp"
#include <d2d1_1.h>
#include <dwrite_3.h>
#include <wrl/client.h>
#include <memory>
#include <unordered_map>
#include <optional>

namespace core {
struct ChatSpan { DWRITE_TEXT_RANGE range; std::uint32_t color; bool bold = false; };
struct ChatInline { UINT32 position; std::string id; float size; };
struct ChatBlock {
    std::wstring text;
    std::vector<ChatSpan> spans;
    std::vector<ChatInline> images;
    std::string mediaId;
    unsigned sourceWidth=0,sourceHeight=0;
    Microsoft::WRL::ComPtr<IDWriteTextLayout> layout;
    std::vector<GlyphOutline> outline;
    float y = 0, height = 0, size = 16, imageWidth=0, inkPadding=0;
    std::uint32_t color = 0xf0f6fc;
    bool author = false, body = false;
    bool outlineBuilt = false;
    float outlineOverride = -1; // Negative inherits chat rules; zero explicitly disables.
};
struct ChatRow {
    std::string id, fingerprint, attention;
    std::vector<ChatBlock> blocks;
    std::wstring time;
    std::wstring icon;
    std::string iconMediaId;
    Microsoft::WRL::ComPtr<IDWriteTextLayout> timeLayout;
    Microsoft::WRL::ComPtr<IDWriteTextLayout> iconLayout;
    float height = 0;
    bool header = false;
};
class ChatView final {
public:
    explicit ChatView(IDWriteFactory* factory);
    void applySettings(const UserSettings& settings);
    bool clickMedia(float x,float y);
    void setSnapshot(std::shared_ptr<const Json> snapshot);
    void setMedia(std::shared_ptr<MediaStore> media) { media_ = std::move(media); }
    bool mediaUpdated();
    std::uint64_t mediaLayoutRevision() const {return media_ ? media_->layoutRevision() : 0;}
    bool hasMedia() const { return media_ != nullptr; }
    bool failedMedia(const ChatBlock& block) const {
        if (!media_) return false;
        for (const auto& item : block.images) if (media_->failed(item.id)) return true;
        return !block.mediaId.empty() && media_->failed(block.mediaId);
    }
    void drawMedia(ID2D1RenderTarget* target);
    void commitMediaVisibility() { if (media_) media_->setVisible(visibleMedia_); }
    ChatBlock externalRuns(yyjson_val* values, float size) const { return runs(values,size,{},false); }
    void layoutExternal(ChatBlock& block, float width) { layout(block,width); }
    void drawExternal(ID2D1RenderTarget* target, ChatBlock& block, float x, float y, D2D1_RECT_F clip);
    void pauseMedia();
    void draw(ID2D1RenderTarget* target, D2D1_RECT_F viewport, bool showScrollbar = true);
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
    bool scrollbarVisible() const { return scrollbarVisible_ && scroll_.maximum()>0; }
    void releaseTargetResources() noexcept;
    bool active() const { return snapshot_ != nullptr; }
    std::size_t messageCount() const { return rows_.size(); }
    const ChatScroll& scrolling() const { return scroll_; }
    std::uint64_t layoutBuilds() const { return layoutBuilds_; }
    std::uint64_t outlineBuilds() const { return outlineBuilds_; }
    std::size_t layoutTextBytes() const;
    std::wstring fontName() const;
private:
    bool scrollbarVisible_=true;
    struct TextAnchor { std::string id, fingerprint; std::size_t block; UINT32 position; float lineOffset; };
    std::optional<TextAnchor> captureReadingAnchor() const;
    void arrange(float width, float height);
    ChatRow makeRow(yyjson_val* value) const;
    ChatBlock runs(yyjson_val* values, float size, std::wstring prefix = {}, bool chatEmoji = true) const;
    ChatBlock mediaBlock(yyjson_val* asset, bool forwarded) const;
    void layout(ChatBlock& block, float width);
    void paintBlock(ID2D1RenderTarget* target, ChatBlock& block, float x, float y);
    void paintMedia(ID2D1RenderTarget* target, const std::string& id, D2D1_RECT_F bounds);
    ID2D1SolidColorBrush* brush(ID2D1RenderTarget* target, std::uint32_t color);
    Microsoft::WRL::ComPtr<IDWriteFactory> factory_;
    Microsoft::WRL::ComPtr<IDWriteFontCollection> fonts_;
    Microsoft::WRL::ComPtr<ID2D1StrokeStyle> outlineStroke_;
    std::unordered_map<std::uint64_t,Microsoft::WRL::ComPtr<IDWriteTextFormat>> formats_;
    Microsoft::WRL::ComPtr<IDWriteTextLayout> loadingLayout_;
    Microsoft::WRL::ComPtr<IDWriteTextLayout> enlargementLayout_;
    std::unordered_map<std::uint32_t,Microsoft::WRL::ComPtr<ID2D1SolidColorBrush>> brushes_;
    std::shared_ptr<const Json> snapshot_;
    std::shared_ptr<MediaStore> media_;
    std::uint64_t mediaRevision_=0;
    struct MediaBitmap { std::size_t index; Microsoft::WRL::ComPtr<ID2D1Bitmap> bitmap; };
    std::unordered_map<std::string,MediaBitmap> mediaBitmaps_;
    std::set<std::string> visibleMedia_;
    std::set<std::string> visibleBitmaps_;
    struct MediaPlacement { std::string id; D2D1_RECT_F bounds, clip; };
    std::vector<MediaPlacement> mediaPlacements_;
    bool recordingMedia_ = false;
    float mediaDpi_ = 96;
    std::vector<ChatRow> rows_;
    ChatScroll scroll_;
    std::string generation_;
    float width_ = 0, viewportHeight_ = 0, fontSize_ = 16;
    unsigned preset_ = 0, rolePosition_ = 0;
    bool changed_ = false, newGeneration_ = true, mentionBackground_ = true;
    bool outlineEnabled_ = true, draggingScrollbar_ = false;
    UserSettings settings_;bool settingsActive_=false;
    std::string enlargedId_;
    float scrollbarGrab_ = 0;
    D2D1_RECT_F viewport_{};
    std::uint64_t layoutBuilds_ = 0;
    std::uint64_t outlineBuilds_ = 0;
    std::optional<TextAnchor> pendingAnchor_;
};
}
