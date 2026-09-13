#pragma once
#include "json.hpp"
#include <chrono>
#include <optional>
#include <span>
#include <vector>

namespace core {
constexpr std::size_t maxFrameBytes = 16 * 1024, maxSnapshotBytes = 1024 * 1024;
constexpr std::string_view coreCapability = "core_render_v1";
std::string sha256(std::span<const unsigned char> bytes);
std::string base64(std::span<const unsigned char> bytes);
std::vector<unsigned char> unbase64(std::string_view encoded);

struct SnapshotDocument {
    std::string generation;
    std::uint64_t revision;
    std::unique_ptr<Json> json;
};

class SnapshotAssembler final {
public:
    // Existing published state is owned separately by the connection and remains
    // unchanged on partial/malformed data. A failed transaction must reconnect.
    std::optional<SnapshotDocument> accept(std::string_view frame);
    void reset() noexcept;
    std::size_t pendingBytes() const noexcept { return bytes_.size(); }
private:
    std::string id_, generation_, digest_;
    std::uint64_t revision_ = 0;
    std::size_t count_ = 0, next_ = 0, total_ = 0;
    std::vector<unsigned char> bytes_;
    std::chrono::steady_clock::time_point started_;
};
std::string coreHello(const SnapshotDocument* previous = nullptr);
}
