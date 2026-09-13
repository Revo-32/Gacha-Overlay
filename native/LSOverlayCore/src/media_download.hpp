#pragma once
#include "transport.hpp"
#include <functional>
#include <unordered_map>
namespace core {
struct DownloadFile {
    std::filesystem::path path;
    std::uint64_t bytes=0;
    ~DownloadFile() { std::error_code error; std::filesystem::remove(path,error); }
};
struct DownloadedMedia { std::shared_ptr<DownloadFile> file; MediaResponse response; };
struct MediaFetcher {
    using Fetch=std::function<DownloadedMedia(const std::string&,unsigned,unsigned,std::stop_token)>;
    Fetch fetch;std::function<void()> clear;
    MediaFetcher()=default;
    template<class F> MediaFetcher(F value):fetch(std::move(value)) {}
    MediaFetcher(Fetch value,std::function<void()> purge):fetch(std::move(value)),clear(std::move(purge)) {}
    explicit operator bool() const {return static_cast<bool>(fetch);}
    DownloadedMedia operator()(const std::string& id,unsigned width,unsigned height,std::stop_token stop) const {return fetch(id,width,height,stop);}
};
struct MediaCacheBudget {
    std::uint64_t hard=1024ULL*1024*1024,high=900ULL*1024*1024,low=768ULL*1024*1024;
    std::size_t files=512;
};
// Called only by MediaStore's two bounded network workers. Never UI or decoder.
MediaFetcher makeMediaFetcher(Endpoint endpoint,std::string_view token,MediaCacheBudget budget={});
}
