#include "media_download.hpp"
#include "media_package.hpp"
#include "auth.hpp"
#include <shlobj.h>

namespace core {
namespace {
class DownloadCache final {
public:
    DownloadCache(Endpoint endpoint,std::string_view token,MediaCacheBudget budget) : http_(std::move(endpoint)),authorization_("Bearer ",token),budget_(budget) {
        if (!budget.low || budget.low>=budget.high || budget.high>=budget.hard || budget.hard>1024ULL*1024*1024 || budget.files<2 || budget.files>512) throw std::runtime_error("Invalid native media cache budget");
        if (!token.starts_with("lso_") || !opaqueSecret(token.substr(4))) throw std::runtime_error("Invalid media credential");
        PWSTR folder=nullptr;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData,KF_FLAG_DEFAULT,nullptr,&folder))) throw std::runtime_error("Core media directory unavailable");
        try { directory_=std::filesystem::path(folder)/L"LSOverlayCore"/L"media"/widen(newInstallationId()); }
        catch (...) { CoTaskMemFree(folder); throw; }
        CoTaskMemFree(folder); validateMediaPath(directory_); std::filesystem::create_directories(directory_);
    }
    ~DownloadCache() { files_.clear();std::error_code error;std::filesystem::remove(directory_,error); }
    void clear() {std::lock_guard lock(mutex_);requests_.clear();files_.clear();}
    DownloadedMedia fetch(const std::string& id,unsigned width,unsigned height,std::stop_token stop) {
        const auto requestKey=id+"/"+std::to_string(width)+"/"+std::to_string(height);
        {
        std::lock_guard lock(mutex_);
        if (const auto alias=requests_.find(requestKey);alias!=requests_.end()) {
            if (const auto cached=files_.find(alias->second);cached!=files_.end()) {
                cached->second.touched=++tick_;return {cached->second.file,cached->second.response};
            }
        }
        }
        auto file=std::make_shared<DownloadFile>(); file->path=directory_/(widen(newInstallationId())+L".lscm");
        std::uint64_t reservation=0;
        try {
        auto response=http_.downloadMedia(id,width,height,authorization_.view(),file->path,stop,[&](std::uint64_t additional) {
            std::lock_guard lock(mutex_);reserve(additional);reserved_+=additional;++pending_;reservation=additional;
        });
        file->bytes=response.bytes;
        { MediaPackage package(file->path); if (package.width()!=response.width || package.height()!=response.height) throw std::runtime_error("Media header/package mismatch"); }
        std::lock_guard lock(mutex_);reserved_-=reservation;--pending_;reservation=0;
        if (auto previous=files_.find(response.key);previous!=files_.end()) {file=previous->second.file;previous->second.touched=++tick_;}
        else files_[response.key]={file,response,++tick_};
        if (requests_.size()>=1024) requests_.erase(requests_.begin());
        requests_[requestKey]=response.key;
        return {std::move(file),std::move(response)};
        } catch (...) {
            if (reservation) {std::lock_guard lock(mutex_);reserved_-=reservation;--pending_;}
            throw;
        }
    }
private:
    void reserve(std::uint64_t additional) {
        const auto hard=budget_.hard,high=budget_.high,low=budget_.low;
        std::uint64_t used=reserved_;for (const auto& pair : files_) used+=pair.second.file->bytes;
        const bool pressure=used+additional>high || files_.size()+pending_>=budget_.files;
        while (pressure && (used+additional>low || files_.size()+pending_>=budget_.files)) {
            auto oldest=files_.end();
            for (auto it=files_.begin();it!=files_.end();++it)
                if (it->second.file.use_count()==1 && (oldest==files_.end() || it->second.touched<oldest->second.touched)) oldest=it;
            if (oldest==files_.end()) break;
            used-=oldest->second.file->bytes;files_.erase(oldest);
        }
        if (used+additional>hard || files_.size()+pending_>=budget_.files) throw TransportError("Visible media leases exhaust the compressed-byte budget",429);
    }
    HttpTransport http_; Secret authorization_; std::filesystem::path directory_;
    struct Cached {std::shared_ptr<DownloadFile> file;MediaResponse response;std::uint64_t touched;};
    std::unordered_map<std::string,Cached> files_;
    std::unordered_map<std::string,std::string> requests_;
    std::uint64_t tick_=0;
    std::mutex mutex_;
    std::uint64_t reserved_=0;
    std::size_t pending_=0;
    MediaCacheBudget budget_;
};
}
MediaFetcher makeMediaFetcher(Endpoint endpoint,std::string_view token,MediaCacheBudget budget) {
    auto cache=std::make_shared<DownloadCache>(std::move(endpoint),token,budget);
    return {[cache](const std::string& id,unsigned width,unsigned height,std::stop_token stop) { return cache->fetch(id,width,height,stop); },[cache] {cache->clear();}};
}
}
