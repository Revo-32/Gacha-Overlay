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
MediaStore::MediaStore(std::function<void()> invalidate) : invalidate_(std::move(invalidate)),remote_(true) {
    worker_=std::jthread([this](std::stop_token stop) { run(stop); });
    for (auto& downloader : downloaders_) downloader=std::jthread([this](std::stop_token stop) { download(stop); });
}
MediaStore::~MediaStore() {
    for (auto& downloader : downloaders_) downloader.request_stop();
    worker_.request_stop(); wake_.notify_all();
    for (auto& downloader : downloaders_) if (downloader.joinable()) downloader.join();
    if (worker_.joinable()) worker_.join();
    // Destroy open package readers before their disposable files and directory.
    sources_.clear(); entries_.clear(); fetcher_={};
}
void MediaStore::setFetcher(MediaFetcher fetcher) { std::lock_guard lock(mutex_);fetcher_=std::move(fetcher);++revision_;wake_.notify_all(); }
void MediaStore::setAnimated(bool enabled) {std::lock_guard lock(mutex_);animated_=enabled;if(!enabled)for(auto& entry:sources_)entry->next.reset();++revision_;wake_.notify_all();}
void MediaStore::clearCache() {
    MediaFetcher fetch;
    {std::lock_guard lock(mutex_);if(!remote_)return;fetch=fetcher_;entries_.clear();for(auto& entry:sources_) {entry->visible=false;entry->current.reset();entry->next.reset();}++layoutRevision_;++revision_;wake_.notify_all();}
    if(fetch.clear)fetch.clear();notify();
}
bool MediaStore::prepare(const std::string& id,unsigned width,unsigned height) {
    if (!remote_) return dimensions(id).has_value();
    if (!validMediaIdentity(id)) return false;
    validateMediaProfile(width,height);
    std::lock_guard lock(mutex_);
    auto found=entries_.find(id);
    if (found!=entries_.end()) {
        if (found->second->requested.width>=width && found->second->requested.height>=height) return !found->second->failed;
        found->second->visible=false; // new physical profile, same logical source
    } else if (entries_.size()>=512) return false;
    auto entry=std::make_shared<Entry>();entry->remote=true;entry->id=id;entry->requested=entry->size={width,height};
    if (found!=entries_.end()) {entry->started=found->second->started;entry->origin=found->second->origin;entry->ready=found->second->ready;}
    entries_[id]=entry;sources_.push_back(entry);++revision_;wake_.notify_all();return true;
}
void MediaStore::retain(const std::set<std::string>& ids) {
    if (!remote_) return;
    std::lock_guard lock(mutex_);
    std::erase_if(entries_,[&](const auto& pair) {return !ids.contains(pair.first);});
    for (const auto& entry : sources_) {
        const bool retained=std::any_of(entries_.begin(),entries_.end(),[&](const auto& pair) {return pair.second==entry;});
        if (!retained) {entry->visible=false;entry->current.reset();entry->next.reset();}
    }
    ++revision_;wake_.notify_all();
}
std::uint64_t MediaStore::layoutRevision() const {std::lock_guard lock(mutex_);return layoutRevision_;}
std::optional<MediaDimensions> MediaStore::dimensions(const std::string& id) const {
    std::lock_guard lock(mutex_); const auto found = entries_.find(id);
    return found == entries_.end() || found->second->failed ? std::nullopt : std::optional(found->second->size);
}
bool MediaStore::failed(const std::string& id) const {
    std::lock_guard lock(mutex_); const auto found = entries_.find(id); return found != entries_.end() && found->second->failed;
}
bool MediaStore::ready(const std::string& id) const {
    std::lock_guard lock(mutex_); const auto found=entries_.find(id);
    return found!=entries_.end() && found->second->ready && !found->second->failed;
}
std::string MediaStore::sourceKey(const std::string& id) const {
    std::lock_guard lock(mutex_);
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
        if(visible && !entry->firstVisible)entry->firstVisible=Clock::now();
        if (!visible) { entry->current.reset(); entry->next.reset(); }
    }
    if (changed) { ++revision_; wake_.notify_all(); }
}
MediaStatistics MediaStore::statistics() const {
    std::lock_guard lock(mutex_); MediaStatistics result{decoded_,published_,failures_,lateFrames_,0,0,downloads_,firstFrames_,downloadMs_,maxDownloadMs_,firstFrameMs_,maxFirstFrameMs_};
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
                if (entry->visible && !entry->failed && (!entry->remote || entry->file)) active.push_back(entry);
                else {
                    entry->package.reset(); entry->timeline.reset();
                    // Offscreen entries keep only identity/timing. The compressed
                    // LRU owns warm files and can evict them under byte pressure.
                    if (entry->remote && !entry->visible) {entry->file.reset();entry->path.clear();}
                }
            }
            std::set<Entry*> retained;for (const auto& pair : entries_) retained.insert(pair.second.get());
            std::erase_if(sources_,[&](const auto& entry) {return entry->remote && !retained.contains(entry.get());});
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
                { std::lock_guard lock(mutex_); if (!entry->started) { entry->started = true; entry->origin = Clock::now(); } }
                const auto elapsed = static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now()-entry->origin).count());
                const bool animate=animated_.load();const auto position = entry->timeline->at(animate?elapsed:0);
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
                        if (!entry->ready) {entry->ready=true;++layoutRevision_;if(entry->firstVisible){const double elapsedMs=std::chrono::duration<double,std::milli>(Clock::now()-*entry->firstVisible).count();++firstFrames_;firstFrameMs_+=elapsedMs;maxFirstFrameMs_=std::max(maxFirstFrameMs_,elapsedMs);}}
                    }
                    notify();
                }
                if (animate && !position.finished) {
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
                { std::lock_guard lock(mutex_); entry->failed = true; entry->current.reset(); entry->next.reset(); ++failures_; ++layoutRevision_; }
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
void MediaStore::download(std::stop_token stop) {
    while (!stop.stop_requested()) {
        std::shared_ptr<Entry> entry; MediaFetcher fetch;std::string requestId;
        {
            std::unique_lock lock(mutex_);
            auto next=Clock::time_point::max();
            if (fetcher_) for (const auto& candidate : sources_) {
                if (!candidate->remote || !candidate->visible || candidate->file || candidate->failed || candidate->downloading) continue;
                if (candidate->retry>Clock::now()) {next=std::min(next,candidate->retry);continue;}
                const auto alias=std::find_if(entries_.begin(),entries_.end(),[&](const auto& pair) {return pair.second==candidate;});
                if (alias==entries_.end()) continue;
                entry=candidate;requestId=alias->first;entry->downloading=true;++entry->attempts;fetch=fetcher_;break;
            }
            if (!entry) {
                const auto revision=revision_;
                if (next==Clock::time_point::max()) wake_.wait(lock,stop,[&] {return revision_!=revision;});
                else wake_.wait_until(lock,stop,next,[&] {return revision_!=revision;});
                continue;
            }
        }
        try {
            const auto fetchStarted=Clock::now();
            auto result=fetch(requestId,entry->requested.width,entry->requested.height,stop);
            std::lock_guard lock(mutex_);
            const double elapsedMs=std::chrono::duration<double,std::milli>(Clock::now()-fetchStarted).count();++downloads_;downloadMs_+=elapsedMs;maxDownloadMs_=std::max(maxDownloadMs_,elapsedMs);
            const auto current=entries_.find(requestId);
            if (current==entries_.end() || current->second!=entry || stop.stop_requested()) {entry->downloading=false;continue;}
            auto shared=std::find_if(sources_.begin(),sources_.end(),[&](const auto& other) {
                return other!=entry && !other->failed && other->file==result.file && other->requested.width==entry->requested.width && other->requested.height==entry->requested.height;
            });
            if (shared!=sources_.end()) {
                (*shared)->visible=(*shared)->visible || entry->visible;entry->visible=false;entry->current.reset();entry->next.reset();entries_[requestId]=*shared;
                ++layoutRevision_;++revision_;wake_.notify_all();
                // All aliases share one decoder/timeline/current-next working set.
            } else {
            entry->path=result.file->path;entry->file=std::move(result.file);entry->size={result.response.width,result.response.height};entry->downloading=false;entry->attempts=0;
            ++layoutRevision_;++revision_;wake_.notify_all();
            }
        } catch (const std::exception& error) {
            if (stop.stop_requested()) return;
            const auto* transport=dynamic_cast<const TransportError*>(&error);
            std::lock_guard lock(mutex_);entry->downloading=false;
            const auto current=entries_.find(requestId);
            if (current==entries_.end() || current->second!=entry) continue; // Superseded request is not a visible failure.
            if (entry->attempts<3 && transport && (transport->httpStatus==0 || transport->httpStatus==429 || transport->httpStatus>=500)) entry->retry=Clock::now()+std::chrono::seconds(2*entry->attempts);
            else {entry->failed=true;++failures_;++layoutRevision_;}
        }
        notify();
    }
}
}
