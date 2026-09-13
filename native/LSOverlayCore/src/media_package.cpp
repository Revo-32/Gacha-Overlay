#include "media_package.hpp"
#include <bcrypt.h>
#include <algorithm>
#include <limits>
#include <stdexcept>

using Microsoft::WRL::ComPtr;
namespace core {
namespace {
void check(HRESULT value) { if (FAILED(value)) throw std::runtime_error("Native media decode failed"); }
template<class T> T read(std::ifstream& file) { T value{}; if (!file.read(reinterpret_cast<char*>(&value),sizeof(value))) throw std::runtime_error("Truncated media index"); return value; }
}
MediaTimeline::MediaTimeline(std::vector<std::uint32_t> durations, std::uint32_t plays) : plays_(plays) {
    if (durations.empty() || durations.size() > 10000 || plays > 0x7fffffffU) throw std::runtime_error("Invalid media timeline");
    for (auto delay : durations) {
        if (delay > 86400000) throw std::runtime_error("Invalid frame duration");
        duration_ += delay; ends_.push_back(duration_);
    }
    if (ends_.size() > 1 && !duration_) throw std::runtime_error("Zero duration animation");
}
FramePosition MediaTimeline::at(std::uint64_t elapsedMs) const {
    if (ends_.size() == 1 || (plays_ && elapsedMs/duration_ >= plays_))
        return {ends_.size()-1,std::numeric_limits<std::uint64_t>::max(),true};
    const auto cycle = elapsedMs/duration_, position = elapsedMs%duration_;
    const auto index = static_cast<std::size_t>(std::upper_bound(ends_.begin(),ends_.end(),position)-ends_.begin());
    return {index,cycle*duration_+ends_[index],false};
}
void validateMediaPath(const std::filesystem::path& path) {
    if (!path.is_absolute()) throw std::runtime_error("Absolute media artifact path required");
    for (auto item = path; !item.empty();) {
        const auto attributes = GetFileAttributesW(item.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) throw std::runtime_error("Media reparse path rejected");
        const auto parent = item.parent_path(); if (parent == item) break; item = parent;
    }
}
MediaPackage::MediaPackage(const std::filesystem::path& path) {
    validateMediaPath(path); file_.open(path,std::ios::binary|std::ios::ate);
    const auto size = file_ ? static_cast<std::streamoff>(file_.tellg()) : 0;
    if (size < 24 || size > 512LL*1024*1024) throw std::runtime_error("Invalid media package size");
    file_.seekg(0); const auto magic = read<std::array<char,8>>(file_);
    constexpr std::array<char,8> expected{'L','S','C','M','E','D','1',0};
    if (magic != expected) throw std::runtime_error("Unknown media package");
    width_ = read<std::uint32_t>(file_); height_ = read<std::uint32_t>(file_);
    const auto count = read<std::uint32_t>(file_); plays_ = read<std::uint32_t>(file_);
    if (!width_ || !height_ || width_ > 8192 || height_ > 8192 || static_cast<std::uint64_t>(width_)*height_ > 16777216 || count < 1 || count > 10000 || plays_ > 0x7fffffffU)
        throw std::runtime_error("Invalid media bounds");
    std::uint64_t offset = 24+static_cast<std::uint64_t>(count)*48;
    if (offset > static_cast<std::uint64_t>(size)) throw std::runtime_error("Media index exceeds file");
    frames_.reserve(count);
    for (std::uint32_t i = 0; i < count; ++i) {
        FrameIndex frame{}; frame.duration = read<std::uint32_t>(file_); frame.offset = read<std::uint64_t>(file_);
        frame.bytes = read<std::uint32_t>(file_); frame.hash = read<std::array<unsigned char,32>>(file_);
        if (frame.duration > 86400000 || frame.offset != offset || frame.bytes < 8 || frame.bytes > 64*1024*1024 || offset+frame.bytes > static_cast<std::uint64_t>(size))
            throw std::runtime_error("Invalid media frame span");
        offset += frame.bytes; frames_.push_back(frame);
    }
    if (offset != static_cast<std::uint64_t>(size)) throw std::runtime_error("Media trailing bytes rejected");
    (void)timeline();
}
MediaTimeline MediaPackage::timeline() const {
    std::vector<std::uint32_t> durations; durations.reserve(frames_.size());
    for (const auto& frame : frames_) durations.push_back(frame.duration);
    return MediaTimeline(std::move(durations),plays_);
}
MediaPixels MediaPackage::decode(IWICImagingFactory* factory, std::size_t index) {
    const auto& frame = frames_.at(index);
    std::vector<unsigned char> encoded(frame.bytes); file_.clear(); file_.seekg(static_cast<std::streamoff>(frame.offset));
    if (!file_.read(reinterpret_cast<char*>(encoded.data()),frame.bytes)) throw std::runtime_error("Truncated media frame");
    std::array<unsigned char,32> digest{};
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE,nullptr,0,encoded.data(),frame.bytes,digest.data(),static_cast<ULONG>(digest.size())) < 0 || digest != frame.hash)
        throw std::runtime_error("Corrupt media checksum");
    ComPtr<IWICStream> stream; check(factory->CreateStream(&stream)); check(stream->InitializeFromMemory(encoded.data(),frame.bytes));
    ComPtr<IWICBitmapDecoder> decoder; check(factory->CreateDecoderFromStream(stream.Get(),nullptr,WICDecodeMetadataCacheOnDemand,&decoder));
    GUID container{}; check(decoder->GetContainerFormat(&container)); UINT count = 0; check(decoder->GetFrameCount(&count));
    if (container != GUID_ContainerFormatPng || count != 1) throw std::runtime_error("Only standalone PNG frames accepted");
    ComPtr<IWICBitmapFrameDecode> bitmap; check(decoder->GetFrame(0,&bitmap)); UINT width = 0,height = 0; check(bitmap->GetSize(&width,&height));
    if (width != width_ || height != height_) throw std::runtime_error("PNG dimensions do not match bounded profile");
    ComPtr<IWICFormatConverter> converter; check(factory->CreateFormatConverter(&converter));
    check(converter->Initialize(bitmap.Get(),GUID_WICPixelFormat32bppPBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));
    MediaPixels pixels{width_,height_,index,std::vector<unsigned char>(static_cast<std::size_t>(width_)*height_*4)};
    check(converter->CopyPixels(nullptr,width_*4,static_cast<UINT>(pixels.bgra.size()),pixels.bgra.data()));
    return pixels;
}
}
