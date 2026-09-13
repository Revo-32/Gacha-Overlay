#pragma once
#include "media_package.hpp"
#include "media_download.hpp"
#include <atomic>
#include <array>
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
struct MediaStatistics { std::uint64_t decoded, published, failures, lateFrames, ownedPixelBytes; unsigned visible;std::uint64_t downloads=0,firstFrames=0;double downloadMs=0,maxDownloadMs=0,firstFrameMs=0,maxFirstFrameMs=0; };
// One decoder thread; file-backed PNG sequence, current + next frame only.
// The directory/manifest is an explicit PRIVATE fixture seam, not a URL API.
class MediaStore final {
public:
    MediaStore(const std::filesystem::path& manifest, std::function<void()> invalidate);
    explicit MediaStore(std::function<void()> invalidate);
    ~MediaStore();
    void setFetcher(MediaFetcher fetcher);
    void setAnimated(bool enabled);
    void clearCache();
    bool prepare(const std::string& id,unsigned width,unsigned height);
    void retain(const std::set<std::string>& ids);
    std::uint64_t layoutRevision() const;
    std::optional<MediaDimensions> dimensions(const std::string& id) const;
    std::shared_ptr<const MediaPixels> frame(const std::string& id) const;
    bool failed(const std::string& id) const;
    bool ready(const std::string& id) const;
    std::string sourceKey(const std::string& id) const;
    void setVisible(const std::set<std::string>& ids);
    void acknowledge() { notificationQueued_ = false; }
    MediaStatistics statistics() const;
private:
    using Clock = std::chrono::steady_clock;
    struct Entry {
        std::filesystem::path path;
        MediaDimensions size;
        std::string id;
        MediaDimensions requested{};
        bool remote=false,downloading=false;
        unsigned attempts=0;
        Clock::time_point retry{};
        std::shared_ptr<DownloadFile> file;
        bool visible = false, failed = false, started = false, ready = false;
        Clock::time_point origin;
        std::optional<Clock::time_point> firstVisible;
        std::shared_ptr<const MediaPixels> current, next;
        std::unique_ptr<MediaPackage> package;
        std::optional<MediaTimeline> timeline;
    };
    void run(std::stop_token stop);
    void download(std::stop_token stop);
    void notify();
    std::unordered_map<std::string,std::shared_ptr<Entry>> entries_;
    std::vector<std::shared_ptr<Entry>> sources_;
    mutable std::mutex mutex_;
    std::condition_variable_any wake_;
    std::function<void()> invalidate_;
    std::atomic_bool notificationQueued_ = false;
    std::jthread worker_;
    MediaFetcher fetcher_;
    std::array<std::jthread,2> downloaders_;
    bool remote_=false;
    std::atomic_bool animated_=true;
    std::uint64_t layoutRevision_=0;
    std::uint64_t revision_ = 0, decoded_ = 0, published_ = 0, failures_ = 0, lateFrames_ = 0;
    std::uint64_t downloads_=0,firstFrames_=0;double downloadMs_=0,maxDownloadMs_=0,firstFrameMs_=0,maxFirstFrameMs_=0;
};
}
