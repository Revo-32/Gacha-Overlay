#pragma once
#include "media_package.hpp"
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <thread>
#include <unordered_map>

namespace core {
struct MediaDimensions { unsigned width, height; };
struct MediaStatistics { std::uint64_t decoded, published, failures, lateFrames, ownedPixelBytes; unsigned visible; };
// One decoder thread; file-backed PNG sequence, current + next frame only.
// The directory/manifest is an explicit PRIVATE fixture seam, not a URL API.
class MediaStore final {
public:
    MediaStore(const std::filesystem::path& manifest, std::function<void()> invalidate);
    ~MediaStore();
    std::optional<MediaDimensions> dimensions(const std::string& id) const;
    std::shared_ptr<const MediaPixels> frame(const std::string& id) const;
    bool failed(const std::string& id) const;
    std::string sourceKey(const std::string& id) const;
    void setVisible(const std::set<std::string>& ids);
    void acknowledge() { notificationQueued_ = false; }
    MediaStatistics statistics() const;
private:
    using Clock = std::chrono::steady_clock;
    struct Entry {
        std::filesystem::path path;
        MediaDimensions size;
        bool visible = false, failed = false, started = false;
        Clock::time_point origin;
        std::shared_ptr<const MediaPixels> current, next;
        std::unique_ptr<MediaPackage> package;
        std::optional<MediaTimeline> timeline;
    };
    void run(std::stop_token stop);
    void notify();
    std::unordered_map<std::string,std::shared_ptr<Entry>> entries_;
    std::vector<std::shared_ptr<Entry>> sources_;
    mutable std::mutex mutex_;
    std::condition_variable_any wake_;
    std::function<void()> invalidate_;
    std::atomic_bool notificationQueued_ = false;
    std::jthread worker_;
    std::uint64_t revision_ = 0, decoded_ = 0, published_ = 0, failures_ = 0, lateFrames_ = 0;
};
}
