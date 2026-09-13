#pragma once
#include <algorithm>
#include <cmath>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <vector>

namespace core {
struct ChatExtent { std::string id; float height; };
struct ChatAnchor { std::string id; float within = 0; };
class ChatScroll final {
public:
    ChatAnchor anchor() const {
        float y = 0;
        for (const auto& row : rows_) { if (y+row.height > offset_) return {row.id,offset_-y}; y += row.height; }
        return {};
    }
    void setRows(std::vector<ChatExtent> rows, float viewport, bool newGeneration = false) {
        if (!std::isfinite(viewport) || viewport < 0) throw std::runtime_error("Invalid chat viewport");
        std::unordered_set<std::string> ids;
        float total = 0;
        for (const auto& row : rows) {
            if (row.id.empty() || !ids.insert(row.id).second || !std::isfinite(row.height) || row.height < 0 || row.height > 1e7f)
                throw std::runtime_error("Invalid chat extent");
            total += row.height;
        }
        const auto previous = anchor();
        if (newGeneration) followLatest();
        if (!following_) {
            std::unordered_set<std::string> old;
            for (const auto& row : rows_) old.insert(row.id);
            for (const auto& row : rows) if (!old.contains(row.id)) unread_ = std::min(20U,unread_+1);
        }
        auto restored = previous;
        if (!following_ && !ids.contains(previous.id)) {
            // Retention/delete: nearest surviving successor, then predecessor.
            auto position = std::find_if(rows_.begin(),rows_.end(),[&](const auto& row) { return row.id == previous.id; });
            if (position != rows_.end()) {
                auto next = std::find_if(position,rows_.end(),[&](const auto& row) { return ids.contains(row.id); });
                if (next != rows_.end()) restored = {next->id,0};
                else while (position != rows_.begin()) { --position; if (ids.contains(position->id)) { restored = {position->id,0}; break; } }
            }
        }
        rows_ = std::move(rows); height_ = total; viewport_ = viewport;
        if (following_) offset_ = maximum(); else restore(restored);
    }
    void restore(const ChatAnchor& anchor) {
        float y = 0;
        for (const auto& row : rows_) {
            if (row.id == anchor.id) { offset_ = std::clamp(y+std::clamp(anchor.within,0.0f,row.height),0.0f,maximum()); return; }
            y += row.height;
        }
        offset_ = std::clamp(offset_,0.0f,maximum());
    }
    void scroll(float delta) {
        if (!std::isfinite(delta)) return;
        offset_ = std::clamp(offset_+delta,0.0f,maximum());
        following_ = maximum()-offset_ <= 2;
        if (following_) { offset_ = maximum(); unread_ = 0; }
    }
    void followLatest() { following_ = true; unread_ = 0; offset_ = maximum(); }
    float maximum() const { return std::max(0.0f,height_-viewport_); }
    float offset() const { return offset_; }
    float totalHeight() const { return height_; }
    bool following() const { return following_; }
    unsigned unread() const { return unread_; }
private:
    std::vector<ChatExtent> rows_;
    float height_ = 0, viewport_ = 0, offset_ = 0;
    bool following_ = true;
    unsigned unread_ = 0;
};
}
