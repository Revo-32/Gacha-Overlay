#include "transport.hpp"
#include "core_wire.hpp"
#include <algorithm>
#include <array>
#include <chrono>
#include <cwctype>
#include <stdexcept>
#include <charconv>

namespace core {
namespace {
void checked(BOOL result) { if (!result) throw TransportError("Windows HTTP operation failed (code " + std::to_string(GetLastError()) + ")"); }
std::int64_t steadyMs() { return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count(); }
void requestPolicy(HINTERNET handle, bool secure) {
    DWORD disabled = WINHTTP_DISABLE_REDIRECTS | WINHTTP_DISABLE_COOKIES | WINHTTP_DISABLE_AUTHENTICATION;
    checked(WinHttpSetOption(handle,WINHTTP_OPTION_DISABLE_FEATURE,&disabled,sizeof(disabled)));
    if (secure) { DWORD enabled = WINHTTP_ENABLE_SSL_REVOCATION; checked(WinHttpSetOption(handle,WINHTTP_OPTION_ENABLE_FEATURE,&enabled,sizeof(enabled))); }
}
void authorize(HINTERNET handle, std::string_view authorization) {
    if (authorization.empty()) return;
    if (authorization.size() > 256 || authorization.find_first_of("\r\n\0",0,3) != std::string_view::npos)
        throw std::runtime_error("Invalid authorization header");
    Secret headerText("Authorization: ",authorization);
    auto header = widen(headerText.view());
    const auto result = WinHttpAddRequestHeaders(handle,header.c_str(),static_cast<DWORD>(header.size()),WINHTTP_ADDREQ_FLAG_ADD | WINHTTP_ADDREQ_FLAG_REPLACE);
    SecureZeroMemory(header.data(),header.size()*sizeof(wchar_t));
    checked(result);
}
DWORD responseStatus(HINTERNET handle) {
    DWORD status = 0, bytes = sizeof(status);
    checked(WinHttpQueryHeaders(handle,WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,WINHTTP_HEADER_NAME_BY_INDEX,&status,&bytes,WINHTTP_NO_HEADER_INDEX));
    return status;
}
}
std::wstring widen(std::string_view text) {
    if (text.empty()) return {};
    if (text.size() > INT_MAX) throw std::runtime_error("UTF-8 input too large");
    const int length = MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),nullptr,0);
    if (length <= 0) throw std::runtime_error("Invalid UTF-8");
    std::wstring output(static_cast<std::size_t>(length),L'\0');
    if (!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),output.data(),length)) throw std::runtime_error("UTF-8 conversion failed");
    return output;
}
std::string narrow(std::wstring_view text) {
    if (text.empty()) return {};
    if (text.size() > INT_MAX) throw std::runtime_error("UTF-16 input too large");
    const int length = WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),nullptr,0,nullptr,nullptr);
    if (length <= 0) throw std::runtime_error("Invalid UTF-16");
    std::string output(static_cast<std::size_t>(length),'\0');
    if (!WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),output.data(),length,nullptr,nullptr)) throw std::runtime_error("UTF-16 conversion failed");
    return output;
}
bool opaqueSecret(std::string_view text, std::size_t length) {
    return text.size() == length && std::all_of(text.begin(),text.end(),[](unsigned char c) {
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
    });
}
std::int64_t utcTicks() {
    FILETIME time{}; GetSystemTimeAsFileTime(&time);
    return static_cast<std::int64_t>((static_cast<std::uint64_t>(time.dwHighDateTime)<<32) | time.dwLowDateTime);
}
std::int64_t parseUtc(std::string_view value) {
    if (value.size() < 20 || value.size() > 40 || value[4]!='-' || value[7]!='-' || value[10]!='T' || value[13]!=':' || value[16]!=':')
        throw std::runtime_error("Invalid UTC timestamp");
    auto digits = [&](std::size_t start, std::size_t count) {
        unsigned result = 0;
        if (start+count > value.size()) throw std::runtime_error("Truncated UTC timestamp");
        for (auto i = start; i < start+count; ++i) { if (value[i]<'0' || value[i]>'9') throw std::runtime_error("Invalid UTC digits"); result = result*10 + static_cast<unsigned>(value[i]-'0'); }
        return static_cast<WORD>(result);
    };
    SYSTEMTIME time{}; time.wYear = digits(0,4); time.wMonth = digits(5,2); time.wDay = digits(8,2);
    time.wHour = digits(11,2); time.wMinute = digits(14,2); time.wSecond = digits(17,2);
    std::size_t position = 19;
    if (value[position] == '.') {
        const auto start = ++position;
        while (position < value.size() && value[position]>='0' && value[position]<='9') ++position;
        if (position == start || position-start > 7) throw std::runtime_error("Invalid fractional UTC seconds");
        for (std::size_t i = 0; i < 3; ++i) time.wMilliseconds = static_cast<WORD>(time.wMilliseconds*10 + (start+i < position ? value[start+i]-'0' : 0));
    }
    if (value.substr(position) != "Z" && value.substr(position) != "+00:00") throw std::runtime_error("Expected explicit UTC timestamp");
    FILETIME file{};
    if (!SystemTimeToFileTime(&time,&file)) throw std::runtime_error("Invalid UTC date");
    return static_cast<std::int64_t>((static_cast<std::uint64_t>(file.dwHighDateTime)<<32) | file.dwLowDateTime);
}
Endpoint::Endpoint(std::wstring_view input) {
    if (input.empty() || input.size() > 2048 || input.find_first_of(L"\r\n\0\\",0,4) != std::wstring_view::npos)
        throw std::runtime_error("Invalid Backend endpoint");
    const std::wstring url(input);
    URL_COMPONENTS parts{}; parts.dwStructSize = sizeof(parts);
    parts.dwHostNameLength = parts.dwUrlPathLength = parts.dwExtraInfoLength = parts.dwUserNameLength = parts.dwPasswordLength = static_cast<DWORD>(-1);
    checked(WinHttpCrackUrl(url.c_str(),static_cast<DWORD>(url.size()),0,&parts));
    if (parts.dwUserNameLength || parts.dwPasswordLength || parts.dwExtraInfoLength || parts.dwHostNameLength == 0 ||
        (parts.dwUrlPathLength != 0 && std::wstring_view(parts.lpszUrlPath,parts.dwUrlPathLength) != L"/"))
        throw std::runtime_error("Backend endpoint must be a credential-free origin");
    host.assign(parts.lpszHostName,parts.dwHostNameLength);
    std::transform(host.begin(),host.end(),host.begin(),[](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
    if (host.front() == L'[' && host.back() == L']') host = host.substr(1,host.size()-2);
    loopback = host == L"localhost" || host == L"127.0.0.1" || host == L"::1";
    secure = parts.nScheme == INTERNET_SCHEME_HTTPS;
    if (!secure && !(parts.nScheme == INTERNET_SCHEME_HTTP && loopback)) throw std::runtime_error("HTTPS required outside explicit loopback");
    port = parts.nPort;
    const auto authority = host.find(L':') == std::wstring::npos ? host : L"[" + host + L"]";
    origin = (secure ? "https://" : "http://") + narrow(authority);
    if (port != (secure ? 443 : 80)) origin += ":" + std::to_string(port);
}
HttpTransport::HttpTransport(Endpoint endpoint) : endpoint_(std::move(endpoint)) {
    session_.reset(WinHttpOpen(L"LSOverlayCore/Native-M2",endpoint_.loopback ? WINHTTP_ACCESS_TYPE_NO_PROXY : WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
        WINHTTP_NO_PROXY_NAME,WINHTTP_NO_PROXY_BYPASS,0));
    checked(session_.get() != nullptr);
    checked(WinHttpSetTimeouts(session_.get(),5000,5000,5000,5000));
    DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3;
    checked(WinHttpSetOption(session_.get(),WINHTTP_OPTION_SECURE_PROTOCOLS,&protocols,sizeof(protocols)));
    connection_.reset(WinHttpConnect(session_.get(),endpoint_.host.c_str(),endpoint_.port,0));
    checked(connection_.get() != nullptr);
}
HttpResponse HttpTransport::request(const wchar_t* method, const std::wstring& path, std::string_view body,
    std::string_view authorization, std::stop_token stop) {
    if (path.empty() || path[0] != L'/' || path.find_first_of(L"\r\n\0",0,3) != std::wstring::npos || body.size() > 65536 || stop.stop_requested())
        throw std::runtime_error("Invalid or cancelled HTTP request");
    InternetHandle request(WinHttpOpenRequest(connection_.get(),method,path.c_str(),nullptr,WINHTTP_NO_REFERER,WINHTTP_DEFAULT_ACCEPT_TYPES,endpoint_.secure ? WINHTTP_FLAG_SECURE : 0));
    checked(request.get() != nullptr);
    std::stop_callback cancel(stop,[&] { request.close(); });
    requestPolicy(request.get(),endpoint_.secure);
    authorize(request.get(),authorization);
    const auto deadline = steadyMs()+15000;
    checked(WinHttpSendRequest(request.get(),L"Content-Type: application/json\r\nAccept: application/json",static_cast<DWORD>(-1),
        body.empty() ? nullptr : const_cast<char*>(body.data()),static_cast<DWORD>(body.size()),static_cast<DWORD>(body.size()),0));
    checked(WinHttpReceiveResponse(request.get(),nullptr));
    HttpResponse response; response.status = responseStatus(request.get());
    if (response.status >= 300 && response.status < 400) throw std::runtime_error("HTTP redirect rejected");
    std::array<char,4096> buffer{};
    for (;;) {
        DWORD count = 0;
        checked(WinHttpReadData(request.get(),buffer.data(),static_cast<DWORD>(buffer.size()),&count));
        if (count == 0) break;
        if (response.body.size()+count > 65536 || steadyMs() > deadline) { SecureZeroMemory(buffer.data(),buffer.size()); throw std::runtime_error("HTTP body/deadline limit exceeded"); }
        response.body.append(buffer.data(),count); SecureZeroMemory(buffer.data(),buffer.size());
    }
    return response;
}
bool validMediaIdentity(std::string_view id,std::size_t length) {
    return id.size()==length && std::all_of(id.begin(),id.end(),[](char c) { return (c>='0' && c<='9') || (c>='a' && c<='f'); });
}
void validateMediaProfile(unsigned width,unsigned height) {
    if (!width || !height || width>8192 || height>8192 || static_cast<std::uint64_t>(width)*height>16777216)
        throw std::runtime_error("Invalid physical media profile");
}
MediaResponse HttpTransport::downloadMedia(std::string_view id,unsigned width,unsigned height,
    std::string_view authorization,const std::filesystem::path& destination,std::stop_token stop,const std::function<void(std::uint64_t)>& reserve) {
    if (!validMediaIdentity(id) || stop.stop_requested()) throw std::runtime_error("Invalid media identity");
    validateMediaProfile(width,height);
    const auto path=L"/api/v1/core/media/"+widen(id)+L"/"+std::to_wstring(width)+L"/"+std::to_wstring(height);
    InternetHandle request(WinHttpOpenRequest(connection_.get(),L"GET",path.c_str(),nullptr,WINHTTP_NO_REFERER,WINHTTP_DEFAULT_ACCEPT_TYPES,endpoint_.secure ? WINHTTP_FLAG_SECURE : 0));
    checked(request.get()!=nullptr); requestPolicy(request.get(),endpoint_.secure); authorize(request.get(),authorization);
    // Conversion may take longer than small JSON requests. Absolute watchdog
    // closes the request even if the peer drips bytes or a WinHTTP call blocks.
    checked(WinHttpSetTimeouts(request.get(),5000,5000,5000,55000));
    std::stop_callback cancel(stop,[&] { request.close(); });
    std::mutex gate; std::condition_variable_any wake;
    std::jthread watchdog([&](std::stop_token done) {
        std::unique_lock lock(gate); wake.wait_for(lock,done,std::chrono::seconds(60),[] { return false; });
        if (!done.stop_requested()) request.close();
    });
    const auto finish=[&] { watchdog.request_stop(); wake.notify_all(); if (watchdog.joinable()) watchdog.join(); };
    HANDLE file=INVALID_HANDLE_VALUE;
    try {
        checked(WinHttpSendRequest(request.get(),L"Accept: application/vnd.lsoverlay.media-v1",static_cast<DWORD>(-1),nullptr,0,0,0));
        checked(WinHttpReceiveResponse(request.get(),nullptr));
        const auto status=responseStatus(request.get());
        if (status!=200) throw TransportError("Media delivery denied",status);
        const auto header=[&](const wchar_t* name) {
            std::array<wchar_t,128> value{}; DWORD bytes=static_cast<DWORD>(sizeof(value)),index=0;
            checked(WinHttpQueryHeaders(request.get(),WINHTTP_QUERY_CUSTOM,name,value.data(),&bytes,&index));
            std::array<wchar_t,128> duplicate{}; DWORD extra=static_cast<DWORD>(sizeof(duplicate));
            if (WinHttpQueryHeaders(request.get(),WINHTTP_QUERY_CUSTOM,name,duplicate.data(),&extra,&index) || GetLastError()!=ERROR_WINHTTP_HEADER_NOT_FOUND)
                throw std::runtime_error("Duplicate media response header");
            return narrow(value.data());
        };
        const auto integer=[&](const wchar_t* name) {
            const auto value=header(name); std::uint64_t number=0;
            const auto parsed=std::from_chars(value.data(),value.data()+value.size(),number);
            if (parsed.ec!=std::errc{} || parsed.ptr!=value.data()+value.size()) throw std::runtime_error("Invalid media numeric header");
            return number;
        };
        if (header(L"Content-Type")!="application/vnd.lsoverlay.media-v1") throw std::runtime_error("Invalid media response type");
        MediaResponse response{}; response.key=header(L"X-Core-Media-Key"); response.bytes=integer(L"Content-Length");
        const auto w=integer(L"X-Core-Media-Width"),h=integer(L"X-Core-Media-Height");
        if (!validMediaIdentity(response.key,64) || response.bytes<24 || response.bytes>512ULL*1024*1024 || w>8192 || h>8192)
            throw std::runtime_error("Media response bounds exceeded");
        response.width=static_cast<unsigned>(w); response.height=static_cast<unsigned>(h); validateMediaProfile(response.width,response.height);
        const auto hit=header(L"X-Core-Cache-Hit"); if (hit!="0" && hit!="1") throw std::runtime_error("Invalid media cache header"); response.cacheHit=hit=="1";
        if (reserve) reserve(response.bytes); // Exact compressed bytes before any disk write.
        file=CreateFileW(destination.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_TEMPORARY,nullptr);
        if (file==INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create private media download");
        std::array<unsigned char,65536> buffer{}; std::uint64_t total=0;
        for (;;) {
            DWORD read=0,written=0; checked(WinHttpReadData(request.get(),buffer.data(),static_cast<DWORD>(buffer.size()),&read));
            if (!read) break;
            total+=read; if (total>response.bytes) throw std::runtime_error("Media response length overflow");
            checked(WriteFile(file,buffer.data(),read,&written,nullptr)); if (written!=read) throw std::runtime_error("Short media file write");
        }
        if (total!=response.bytes || stop.stop_requested()) throw std::runtime_error("Incomplete media response");
        CloseHandle(file); file=INVALID_HANDLE_VALUE; finish(); return response;
    } catch (...) {
        if (file!=INVALID_HANDLE_VALUE) { CloseHandle(file); DeleteFileW(destination.c_str()); }
        finish(); throw;
    }
}
WebSocket::WebSocket(HttpTransport& http, std::string_view accessToken, std::stop_token stop) {
    if (!accessToken.starts_with("lso_") || !opaqueSecret(accessToken.substr(4))) throw std::runtime_error("Invalid Remote credential shape");
    InternetHandle request(WinHttpOpenRequest(http.connection(),L"GET",L"/api/v1/core/stream",nullptr,WINHTTP_NO_REFERER,WINHTTP_DEFAULT_ACCEPT_TYPES,http.endpoint().secure ? WINHTTP_FLAG_SECURE : 0));
    checked(request.get() != nullptr);
    std::stop_callback cancel(stop,[&] { request.close(); });
    requestPolicy(request.get(),http.endpoint().secure);
    Secret auth("Bearer ",accessToken); authorize(request.get(),auth.view());
    checked(WinHttpSetOption(request.get(),WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET,nullptr,0));
    checked(WinHttpAddRequestHeaders(request.get(),L"Sec-WebSocket-Protocol: ls-overlay.v1",static_cast<DWORD>(-1),WINHTTP_ADDREQ_FLAG_ADD));
    checked(WinHttpSendRequest(request.get(),WINHTTP_NO_ADDITIONAL_HEADERS,0,nullptr,0,0,0));
    checked(WinHttpReceiveResponse(request.get(),nullptr));
    const auto status = responseStatus(request.get());
    if (status != 101) throw TransportError("WebSocket upgrade denied (HTTP " + std::to_string(status) + ")",status);
    std::array<wchar_t,64> protocol{}; DWORD length = static_cast<DWORD>(protocol.size()*sizeof(wchar_t));
    checked(WinHttpQueryHeaders(request.get(),WINHTTP_QUERY_CUSTOM,L"Sec-WebSocket-Protocol",protocol.data(),&length,WINHTTP_NO_HEADER_INDEX));
    if (std::wstring_view(protocol.data()) != L"ls-overlay.v1") throw std::runtime_error("WebSocket subprotocol mismatch");
    socket_.reset(WinHttpWebSocketCompleteUpgrade(request.get(),0)); checked(socket_.get() != nullptr);
    deadlineMs_ = steadyMs()+20000;
    watchdog_ = std::jthread([this](std::stop_token token) {
        std::unique_lock lock(mutex_);
        while (!token.stop_requested()) {
            wake_.wait_for(lock,token,std::chrono::seconds(1),[] { return false; });
            if (!token.stop_requested() && steadyMs() > deadlineMs_.load()) { socket_.close(); break; }
        }
    });
}
WebSocket::~WebSocket() { close(); }
void WebSocket::close() noexcept {
    watchdog_.request_stop(); wake_.notify_all();
    socket_.close();
    if (watchdog_.joinable()) watchdog_.join();
}
void WebSocket::send(std::string_view frame) {
    if (frame.empty() || frame.size() > maxFrameBytes) throw std::runtime_error("Outbound Core frame exceeds limit");
    if (WinHttpWebSocketSend(socket_.get(),WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE,const_cast<char*>(frame.data()),static_cast<DWORD>(frame.size())) != NO_ERROR)
        throw TransportError("WebSocket send failed");
}
std::string WebSocket::receive(std::stop_token stop) {
    if (stop.stop_requested()) throw std::runtime_error("WebSocket cancelled");
    std::stop_callback cancel(stop,[&] { socket_.close(); });
    std::array<char,4096> buffer{}; std::string result;
    for (;;) {
        DWORD count = 0; WINHTTP_WEB_SOCKET_BUFFER_TYPE type{};
        if (WinHttpWebSocketReceive(socket_.get(),buffer.data(),static_cast<DWORD>(buffer.size()),&count,&type) != NO_ERROR)
            throw TransportError("WebSocket receive failed or timed out");
        if (type == WINHTTP_WEB_SOCKET_CLOSE_BUFFER_TYPE) throw TransportError("WebSocket closed by peer");
        if (type != WINHTTP_WEB_SOCKET_UTF8_FRAGMENT_BUFFER_TYPE && type != WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE)
            throw std::runtime_error("Binary WebSocket input rejected");
        if (result.size()+count > maxFrameBytes) throw std::runtime_error("Inbound WebSocket exceeds 16 KiB");
        result.append(buffer.data(),count);
        if (type == WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE) { deadlineMs_ = steadyMs()+20000; return result; }
    }
}
}
