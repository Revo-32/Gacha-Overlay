#pragma once
#include <windows.h>
#include <winhttp.h>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <mutex>
#include <stop_token>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>

namespace core {
class TransportError : public std::runtime_error {
public:
    explicit TransportError(const std::string& message, DWORD status = 0) : std::runtime_error(message), httpStatus(status) {}
    DWORD httpStatus;
};
std::wstring widen(std::string_view utf8);
std::string narrow(std::wstring_view utf16);
std::int64_t utcTicks();
std::int64_t parseUtc(std::string_view timestamp);
bool opaqueSecret(std::string_view text, std::size_t length = 43);

class Secret final {
public:
    explicit Secret(std::string_view value = {}) : value_(value) {}
    Secret(std::string_view prefix, std::string_view value) { value_.reserve(prefix.size()+value.size()); value_.append(prefix); value_.append(value); }
    ~Secret() { if (!value_.empty()) SecureZeroMemory(value_.data(),value_.size()); }
    Secret(const Secret&) = delete;
    Secret& operator=(const Secret&) = delete;
    Secret(Secret&&) noexcept = default;
    Secret& operator=(Secret&& other) noexcept {
        if (this != &other) { if (!value_.empty()) SecureZeroMemory(value_.data(),value_.size()); value_ = std::move(other.value_); }
        return *this;
    }
    std::string_view view() const noexcept { return value_; }
private:
    std::string value_;
};

struct Endpoint {
    explicit Endpoint(std::wstring_view url);
    std::wstring host;
    std::string origin;
    INTERNET_PORT port = 0;
    bool secure = false, loopback = false;
};

class InternetHandle final {
public:
    explicit InternetHandle(HINTERNET handle = nullptr) : handle_(handle) {}
    ~InternetHandle() { close(); }
    InternetHandle(const InternetHandle&) = delete;
    InternetHandle& operator=(const InternetHandle&) = delete;
    HINTERNET get() const noexcept { return handle_.load(); }
    void reset(HINTERNET handle) { close(); handle_.store(handle); }
    void close() noexcept { if (const auto old = handle_.exchange(nullptr)) WinHttpCloseHandle(old); }
private:
    std::atomic<HINTERNET> handle_;
};

struct HttpResponse {
    DWORD status = 0;
    std::string body;
    ~HttpResponse() { if (!body.empty()) SecureZeroMemory(body.data(),body.size()); }
    HttpResponse() = default;
    HttpResponse(HttpResponse&&) noexcept = default;
    HttpResponse& operator=(HttpResponse&&) = delete;
};

class HttpTransport final {
public:
    explicit HttpTransport(Endpoint endpoint);
    HttpResponse request(const wchar_t* method, const std::wstring& path, std::string_view body = {},
        std::string_view authorization = {}, std::stop_token stop = {});
    const Endpoint& endpoint() const noexcept { return endpoint_; }
    HINTERNET connection() const noexcept { return connection_.get(); }
private:
    Endpoint endpoint_;
    InternetHandle session_, connection_;
};

class WebSocket final {
public:
    WebSocket(HttpTransport& http, std::string_view accessToken, std::stop_token stop = {});
    ~WebSocket();
    void send(std::string_view frame);
    std::string receive(std::stop_token stop = {});
    void close() noexcept;
private:
    InternetHandle socket_;
    std::atomic<std::int64_t> deadlineMs_{0};
    std::mutex mutex_;
    std::condition_variable_any wake_;
    std::jthread watchdog_;
};
}
