#pragma once
#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <array>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <vector>

namespace core {
struct FrameIndex { std::uint32_t duration; std::uint64_t offset; std::uint32_t bytes; std::array<unsigned char,32> hash; };
struct FramePosition { std::size_t index; std::uint64_t nextMs; bool finished; };
class MediaTimeline final {
public:
    MediaTimeline(std::vector<std::uint32_t> durations, std::uint32_t plays);
    FramePosition at(std::uint64_t elapsedMs) const;
    std::size_t next(std::size_t current) const { return (current+1)%ends_.size(); }
    std::uint64_t duration() const { return duration_; }
private:
    std::vector<std::uint64_t> ends_;
    std::uint64_t duration_ = 0;
    std::uint32_t plays_;
};
struct MediaPixels { std::uint32_t width, height; std::size_t index; std::vector<unsigned char> bgra; };
class MediaPackage final {
public:
    explicit MediaPackage(const std::filesystem::path& path);
    MediaPixels decode(IWICImagingFactory* factory, std::size_t index);
    MediaTimeline timeline() const;
    std::uint32_t width() const { return width_; }
    std::uint32_t height() const { return height_; }
    std::size_t count() const { return frames_.size(); }
private:
    std::ifstream file_;
    std::uint32_t width_ = 0, height_ = 0, plays_ = 0;
    std::vector<FrameIndex> frames_;
};
void validateMediaPath(const std::filesystem::path& path);
}
