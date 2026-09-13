#include "media_store.hpp"
#include "json.hpp"
#include "transport.hpp"
#include <algorithm>

namespace core {
MediaStore::MediaStore(const std::filesystem::path& manifest, std::function<void()> invalidate) : invalidate_(std::move(invalidate)) {
    validateMediaPath(manifest);
    std::ifstream input(manifest,std::ios::binary|std::ios::ate);
    const auto length = input ? static_cast<std::streamoff>(input.tellg()) : 0;
    if (length <= 0 || length > 65536) throw std::runtime_error("Invalid private media manifest size");
    input.seekg(0); std::string bytes(static_cast<std::size_t>(length),'\0');
    if (!input.read(bytes.data(),length)) throw std::runtime_error("Cannot read media manifest");
    Json data(bytes); auto* root = data.root();
    if (!yyjson_is_arr(root) || yyjson_arr_size(root) > 512) throw std::runtime_error("Invalid bounded media manifest");
    std::size_t index = 0,count = 0; yyjson_val* item = nullptr;
    std::unordered_map<std::string,std::shared_ptr<Entry>> byFile;
    yyjson_arr_foreach(root,index,count,item) {
        const std::string id(textField(item,"id")), name(textField(item,"file"));
        if (id.empty() || id.size() > 256 || name.empty() || name.size() > 80 || !name.ends_with(".lscm") ||
            !std::all_of(name.begin(),name.end(),[](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-'; }) || name.find("..") != std::string::npos)
            throw std::runtime_error("Invalid media identity/local file mapping");
        const auto w = numberField(item,"width"), h = numberField(item,"height");
        if (w < 1 || h < 1 || w > 8192 || h > 8192 || w*h > 16777216) throw std::runtime_error("Invalid media manifest dimensions");
        auto entry = std::make_shared<Entry>(); entry->path = manifest.parent_path()/widen(name); validateMediaPath(entry->path);
        entry->size = {static_cast<unsigned>(w),static_cast<unsigned>(h)};
        if (const auto previous = byFile.find(name); previous != byFile.end()) {
            if (previous->second->size.width != entry->size.width || previous->second->size.height != entry->size.height) throw std::runtime_error("Conflicting derivative profile aliases");
            entry = previous->second;
        } else { byFile.emplace(name,entry); sources_.push_back(entry); }
        if (!entries_.emplace(id,std::move(entry)).second) throw std::runtime_error("Duplicate media identity");
    }
    worker_ = std::jthread([this](std::stop_token stop) { run(stop); });
}
MediaStore::~MediaStore() { worker_.request_stop(); wake_.notify_all(); if (worker_.joinable()) worker_.join(); }
std::optional<MediaDimensions> MediaStore::dimensions(const std::string& id) const {
    std::lock_guard lock(mutex_); const auto found = entries_.find(id);
    return found == entries_.end() || found->second->failed ? std::nullopt : std::optional(found->second->size);
}
bool MediaStore::failed(const std::string& id) const {
    std::lock_guard lock(mutex_); const auto found = entries_.find(id); return found != entries_.end() && found->second->failed;
}
std::string MediaStore::sourceKey(const std::string& id) const {
    const auto found = entries_.find(id); return found == entries_.end() ? std::string{} : narrow(found->second->path.filename().wstring());
}
std::shared_ptr<const MediaPixels> MediaStore::frame(const std::string& id) const {
    std::lock_guard lock(mutex_); const auto found = entries_.find(id); return found == entries_.end() ? nullptr : found->second->current;
}
void MediaStore::setVisible(const std::set<std::string>& ids) {
    if (ids.size() > 128) throw std::runtime_error("Visible media request budget exceeded");
    std::lock_guard lock(mutex_); bool changed = false;
    std::set<Entry*> wanted;
    for (const auto& id : ids) if (const auto found = entries_.find(id); found != entries_.end()) wanted.insert(found->second.get());
    for (auto& entry : sources_) {
        const bool visible = wanted.contains(entry.get());
        if (entry->visible == visible) continue;
        changed = true; entry->visible = visible;
        if (!visible) { entry->current.reset(); entry->next.reset(); }
    }
    if (changed) { ++revision_; wake_.notify_all(); }
}
MediaStatistics MediaStore::statistics() const {
    std::lock_guard lock(mutex_); MediaStatistics result{decoded_,published_,failures_,lateFrames_,0,0};
    for (const auto& entry : sources_) {
        if (entry->visible) ++result.visible;
        if (entry->current) result.ownedPixelBytes += entry->current->bgra.size();
        if (entry->next) result.ownedPixelBytes += entry->next->bgra.size();
    }
    return result;
}
void MediaStore::notify() { if (!notificationQueued_.exchange(true)) invalidate_(); }
void MediaStore::run(std::stop_token stop) {
    const auto initialized = CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    Microsoft::WRL::ComPtr<IWICImagingFactory> factory;
    const auto created = SUCCEEDED(initialized) ? CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory)) : initialized;
    if (FAILED(created)) {
        { std::lock_guard lock(mutex_); for (auto& entry : sources_) { entry->failed = true; ++failures_; } }
        notify(); if (SUCCEEDED(initialized)) CoUninitialize(); return;
    }
    while (!stop.stop_requested()) {
        std::vector<std::shared_ptr<Entry>> active;
        std::uint64_t revision = 0;
        {
            std::lock_guard lock(mutex_); revision = revision_;
            for (auto& entry : sources_) {
                if (entry->visible && !entry->failed) active.push_back(entry);
                else { entry->package.reset(); entry->timeline.reset(); }
            }
        }
        auto deadline = Clock::time_point::max();
        for (const auto& entry : active) {
            if (stop.stop_requested()) break;
            try {
                if (!entry->package) {
                    entry->package = std::make_unique<MediaPackage>(entry->path);
                    if (entry->package->width() != entry->size.width || entry->package->height() != entry->size.height) throw std::runtime_error("Media profile mismatch");
                    entry->timeline = entry->package->timeline();
                }
                if (!entry->started) { entry->started = true; entry->origin = Clock::now(); }
                const auto elapsed = static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now()-entry->origin).count());
                const auto position = entry->timeline->at(elapsed);
                std::shared_ptr<const MediaPixels> pixels;
                bool needed = false;
                {
                    std::lock_guard lock(mutex_);
                    if (!entry->visible) continue;
                    needed = !entry->current || entry->current->index != position.index;
                    if (needed && entry->next && entry->next->index == position.index) pixels = std::move(entry->next);
                }
                if (needed) {
                    if (!pixels) { pixels = std::make_shared<MediaPixels>(entry->package->decode(factory.Get(),position.index)); std::lock_guard lock(mutex_); ++decoded_; }
                    {
                        std::lock_guard lock(mutex_);
                        if (!entry->visible) continue;
                        if (entry->current && entry->timeline->next(entry->current->index) != position.index) ++lateFrames_;
                        entry->current = std::move(pixels); ++published_;
                    }
                    notify();
                }
                if (!position.finished) {
                    deadline = std::min(deadline,entry->origin+std::chrono::milliseconds(position.nextMs));
                    const auto next = entry->timeline->next(position.index);
                    bool prepare = false;
                    { std::lock_guard lock(mutex_); prepare = entry->visible && (!entry->next || entry->next->index != next); }
                    if (prepare) {
                        auto prepared = std::make_shared<MediaPixels>(entry->package->decode(factory.Get(),next));
                        std::lock_guard lock(mutex_); ++decoded_; if (entry->visible) entry->next = std::move(prepared);
                    }
                }
            } catch (const std::exception&) {
                { std::lock_guard lock(mutex_); entry->failed = true; entry->current.reset(); entry->next.reset(); ++failures_; }
                notify();
            }
        }
        std::unique_lock lock(mutex_);
        // No polling/FPS cap: only original frame deadlines or real invalidations.
        if (deadline == Clock::time_point::max()) wake_.wait(lock,stop,[&] { return revision_ != revision; });
        else wake_.wait_until(lock,stop,deadline,[&] { return revision_ != revision; });
    }
    factory.Reset(); CoUninitialize();
}
}
