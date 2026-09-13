#include "core_wire.hpp"
#include <bcrypt.h>
#include <wincrypt.h>
#include <algorithm>
#include <array>

namespace core {
std::string sha256(std::span<const unsigned char> bytes) {
    std::array<unsigned char,32> hash{};
    if (bytes.size() > MAXDWORD || BCryptHash(BCRYPT_SHA256_ALG_HANDLE,nullptr,0,
        const_cast<PUCHAR>(bytes.data()),static_cast<ULONG>(bytes.size()),hash.data(),static_cast<ULONG>(hash.size())) < 0)
        throw std::runtime_error("SHA-256 failed");
    constexpr char hex[] = "0123456789abcdef";
    std::string output; output.reserve(64);
    for (const auto value : hash) { output += hex[value >> 4]; output += hex[value & 15]; }
    return output;
}
std::string base64(std::span<const unsigned char> bytes) {
    if (bytes.size() > MAXDWORD) throw std::runtime_error("Base64 input too large");
    DWORD count = 0;
    constexpr DWORD flags = CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF;
    if (!CryptBinaryToStringA(bytes.data(),static_cast<DWORD>(bytes.size()),flags,nullptr,&count)) throw std::runtime_error("Base64 encoding failed");
    std::string result(count,'\0');
    if (!CryptBinaryToStringA(bytes.data(),static_cast<DWORD>(bytes.size()),flags,result.data(),&count)) throw std::runtime_error("Base64 encoding failed");
    result.resize(count); return result;
}
std::vector<unsigned char> unbase64(std::string_view encoded) {
    if (encoded.empty() || encoded.size() > maxFrameBytes || encoded.size() % 4 != 0) throw std::runtime_error("Invalid base64 size");
    DWORD count = 0;
    if (!CryptStringToBinaryA(encoded.data(),static_cast<DWORD>(encoded.size()),CRYPT_STRING_BASE64 | CRYPT_STRING_STRICT,nullptr,&count,nullptr,nullptr))
        throw std::runtime_error("Invalid base64");
    std::vector<unsigned char> result(count);
    if (!CryptStringToBinaryA(encoded.data(),static_cast<DWORD>(encoded.size()),CRYPT_STRING_BASE64 | CRYPT_STRING_STRICT,result.data(),&count,nullptr,nullptr))
        throw std::runtime_error("Invalid base64");
    result.resize(count);
    if (base64(result) != encoded) throw std::runtime_error("Noncanonical base64 rejected");
    return result;
}
void SnapshotAssembler::reset() noexcept {
    id_.clear(); generation_.clear(); digest_.clear(); revision_ = 0; count_ = next_ = total_ = 0;
    std::vector<unsigned char>().swap(bytes_);
}
std::optional<SnapshotDocument> SnapshotAssembler::accept(std::string_view frame) {
    try {
        Json parsed(frame,maxFrameBytes);
        auto* root = parsed.root();
        if (numberField(root,"protocolVersion") != 1 || textField(root,"type") != "core_snapshot_chunk_v1")
            throw std::runtime_error("Unsupported Core wire envelope");
        const auto id = textField(root,"snapshotId"), generation = textField(root,"generation"), digest = textField(root,"sha256");
        const auto index = numberField(root,"index"), count = numberField(root,"count"), total = numberField(root,"totalBytes"), revision = numberField(root,"revision");
        const auto hex = [](unsigned char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); };
        if (id.size() != 32 || !std::all_of(id.begin(),id.end(),hex) || generation.empty() || generation.size() > 64 ||
            digest.size() != 64 || !std::all_of(digest.begin(),digest.end(),hex) || count == 0 || count > 128 ||
            total == 0 || total > maxSnapshotBytes || index >= count || count != (total + 8999) / 9000)
            throw std::runtime_error("Invalid Core snapshot bounds");
        if (id_.empty()) {
            if (index != 0) throw std::runtime_error("Snapshot must start at index zero");
            id_ = id; generation_ = generation; digest_ = digest; revision_ = revision;
            count_ = static_cast<std::size_t>(count); total_ = static_cast<std::size_t>(total);
            bytes_.reserve(total_); started_ = std::chrono::steady_clock::now();
        }
        if (std::chrono::steady_clock::now()-started_ > std::chrono::seconds(20) || id != id_ ||
            generation != generation_ || digest != digest_ || revision != revision_ || count != count_ || total != total_ || index != next_)
            throw std::runtime_error("Mixed, reordered or expired snapshot transaction");
        const auto payload = unbase64(textField(root,"payloadBase64"));
        if (payload.size() != std::min<std::size_t>(9000,total_-bytes_.size())) throw std::runtime_error("Invalid Core payload chunk length");
        bytes_.insert(bytes_.end(),payload.begin(),payload.end()); ++next_;
        if (next_ != count_) return std::nullopt;
        if (bytes_.size() != total_ || sha256(bytes_) != digest_) throw std::runtime_error("Core snapshot integrity mismatch");
        auto document = std::make_shared<Json>(std::string_view(reinterpret_cast<const char*>(bytes_.data()),bytes_.size()));
        auto* data = document->root();
        if (numberField(data,"protocolVersion") != 1 || textField(data,"generation") != generation_ ||
            numberField(data,"revision") != revision_ || textField(data,"selfUserId").empty() ||
            !yyjson_is_arr(field(data,"chat")) || !yyjson_is_obj(field(data,"sales")) || !yyjson_is_arr(field(data,"session")))
            throw std::runtime_error("Core snapshot content identity/schema mismatch");
        SnapshotDocument result{generation_,revision_,std::move(document)};
        reset(); return result;
    } catch (...) { reset(); throw; }
}
std::string coreHello(const SnapshotDocument* previous) {
    std::string result = "{\"protocolVersion\":1,\"type\":\"core_session_start_v1\",\"capabilities\":[\"core_render_v1\"]";
    if (previous) result += ",\"generation\":" + jsonQuote(previous->generation) + ",\"afterRevision\":" + std::to_string(previous->revision);
    return result + "}";
}
}
