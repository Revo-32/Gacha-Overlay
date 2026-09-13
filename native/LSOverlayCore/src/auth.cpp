#include "auth.hpp"
#include "core_wire.hpp"
#include <shlobj.h>
#include <wincrypt.h>
#include <algorithm>
#include <array>
#include <unordered_map>

namespace core {
namespace {
struct FileHandle {
    HANDLE value = INVALID_HANDLE_VALUE;
    ~FileHandle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct ProtectedBlob {
    DATA_BLOB value{};
    ~ProtectedBlob() { if (value.pbData) { SecureZeroMemory(value.pbData,value.cbData); LocalFree(value.pbData); } }
};
std::string percentDecode(std::string_view input) {
    const auto hex = [](char c) -> int { if (c >= '0' && c <= '9') return c-'0'; if (c >= 'a' && c <= 'f') return c-'a'+10; if (c >= 'A' && c <= 'F') return c-'A'+10; return -1; };
    std::string output; output.reserve(input.size());
    for (std::size_t i = 0; i < input.size(); ++i) {
        if (input[i] == '%') {
            if (i+2 >= input.size() || hex(input[i+1]) < 0 || hex(input[i+2]) < 0) throw std::runtime_error("Invalid OAuth URL encoding");
            output += static_cast<char>(hex(input[i+1])*16+hex(input[i+2])); i += 2;
        } else output += input[i];
    }
    if (output.find_first_of("\r\n\0",0,3) != std::string::npos) throw std::runtime_error("Invalid OAuth URL character");
    return output;
}
std::span<const unsigned char> asBytes(std::string_view text) { return {reinterpret_cast<const unsigned char*>(text.data()),text.size()}; }
void validateCredential(const Credential& credential) {
    if (!credential.token.view().starts_with("lso_") || !opaqueSecret(credential.token.view().substr(4)) ||
        credential.expiresTicks <= utcTicks() || !validGuid(credential.installationId))
        throw std::runtime_error("Remote credential invalid or expired");
}
void atomicEncryptedWrite(const std::filesystem::path& path, const DATA_BLOB& blob) {
    const auto temporary = path.parent_path() / (L"credential-" + widen(newInstallationId()) + L".tmp");
    try {
        {
            FileHandle file{CreateFileW(temporary.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr)};
            if (file.value == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create Core credential temporary file");
            DWORD written = 0;
            if (!WriteFile(file.value,blob.pbData,blob.cbData,&written,nullptr) || written != blob.cbData || !FlushFileBuffers(file.value))
                throw std::runtime_error("Core credential write failed");
        }
        if (!MoveFileExW(temporary.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            throw std::runtime_error("Atomic Core credential replacement failed");
    } catch (...) { DeleteFileW(temporary.c_str()); throw; } // Only our newly-created temporary file.
}
}
bool validGuid(std::string_view value) {
    if (value.size() != 36 || value == "00000000-0000-0000-0000-000000000000") return false;
    for (std::size_t i = 0; i < value.size(); ++i) {
        if (i == 8 || i == 13 || i == 18 || i == 23) { if (value[i] != '-') return false; }
        else if (!((value[i]>='0' && value[i]<='9') || (value[i]>='a' && value[i]<='f') || (value[i]>='A' && value[i]<='F'))) return false;
    }
    return true;
}
std::string newInstallationId() {
    GUID guid{}; if (FAILED(CoCreateGuid(&guid))) throw std::runtime_error("Core identity creation failed");
    std::array<wchar_t,40> text{};
    if (!StringFromGUID2(guid,text.data(),static_cast<int>(text.size()))) throw std::runtime_error("Core identity formatting failed");
    return narrow(std::wstring_view(text.data()+1,36));
}
void validateAuthSession(const AuthSession& session, const Endpoint& endpoint) {
    if (!validGuid(session.id) || !opaqueSecret(session.claim.view()) || session.expiresTicks <= utcTicks() ||
        session.expiresTicks > utcTicks()+6LL*60*10000000 || session.authorizationUrl.size() > 4096)
        throw std::runtime_error("Invalid browser authentication session");
    const auto url = widen(session.authorizationUrl);
    URL_COMPONENTS parts{}; parts.dwStructSize = sizeof(parts);
    parts.dwHostNameLength = parts.dwUrlPathLength = parts.dwExtraInfoLength = parts.dwUserNameLength = parts.dwPasswordLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(url.c_str(),static_cast<DWORD>(url.size()),0,&parts) || parts.nScheme != INTERNET_SCHEME_HTTPS || parts.nPort != 443 ||
        std::wstring_view(parts.lpszHostName,parts.dwHostNameLength) != L"discord.com" || parts.dwUserNameLength || parts.dwPasswordLength ||
        std::wstring_view(parts.lpszUrlPath,parts.dwUrlPathLength) != L"/oauth2/authorize" || session.authorizationUrl.find('#') != std::string::npos)
        throw std::runtime_error("Untrusted OAuth authorization URL");
    const auto query = narrow(std::wstring_view(parts.lpszExtraInfo,parts.dwExtraInfoLength));
    if (query.empty() || query.front() != '?') throw std::runtime_error("Missing OAuth query");
    std::unordered_map<std::string,std::string> fields;
    std::size_t offset = 1;
    while (offset < query.size()) {
        const auto end = query.find('&',offset);
        const auto item = std::string_view(query).substr(offset,end == std::string::npos ? query.size()-offset : end-offset);
        const auto split = item.find('=');
        if (split == std::string_view::npos || !fields.emplace(percentDecode(item.substr(0,split)),percentDecode(item.substr(split+1))).second)
            throw std::runtime_error("Malformed or duplicate OAuth field");
        if (end == std::string::npos) break;
        offset = end+1;
        if (offset == query.size()) throw std::runtime_error("Trailing OAuth field separator");
    }
    const auto id = fields["client_id"];
    if (fields.size() != 7 || id.empty() || id.size() > 20 || !std::all_of(id.begin(),id.end(),[](char c) { return c>='0' && c<='9'; }) ||
        std::stoull(id) == 0 || fields["scope"] != "identify" || fields["response_type"] != "code" ||
        fields["redirect_uri"] != endpoint.origin + "/auth/discord/callback" || !opaqueSecret(fields["state"]) ||
        fields["state"] == session.claim.view() || fields["code_challenge_method"] != "S256" || !opaqueSecret(fields["code_challenge"]))
        throw std::runtime_error("OAuth contract mismatch");
}
AuthSession AuthClient::start(std::string_view installationId, std::stop_token stop) {
    if (!validGuid(installationId)) throw std::runtime_error("Invalid Core installation identity");
    auto response = http_.request(L"POST",L"/api/v1/auth/discord/sessions",
        "{\"protocolVersion\":1,\"clientInstallationId\":" + jsonQuote(installationId) + "}",{},stop);
    if (response.status != 200) throw std::runtime_error("OAuth start denied (HTTP " + std::to_string(response.status) + ")");
    Json json(response.body,65536); auto* root = json.root();
    if (numberField(root,"protocolVersion") != 1) throw std::runtime_error("OAuth protocol version mismatch");
    AuthSession session{std::string(textField(root,"sessionId")),Secret(textField(root,"claimSecret")),
        std::string(textField(root,"authorizationUrl")),parseUtc(textField(root,"expiresAt"))};
    validateAuthSession(session,http_.endpoint()); return session;
}
std::optional<Credential> AuthClient::poll(const AuthSession& session, std::string_view installationId, std::stop_token stop) {
    if (session.expiresTicks <= utcTicks() || !validGuid(installationId) || !validGuid(session.id) || !opaqueSecret(session.claim.view()))
        throw std::runtime_error("OAuth session expired or invalid identity");
    Secret header("LSOAuthClaim ",session.claim.view());
    auto response = http_.request(L"GET",L"/api/v1/auth/discord/sessions/"+widen(session.id),{},header.view(),stop);
    if (response.status != 200) throw std::runtime_error("OAuth claim denied (HTTP " + std::to_string(response.status) + ")");
    Json json(response.body,65536); auto* root = json.root();
    if (numberField(root,"protocolVersion") != 1) throw std::runtime_error("OAuth claim version mismatch");
    const auto status = textField(root,"status");
    if (status == "pending") return std::nullopt;
    if (status != "approved" || textField(root,"failure") != "none") throw std::runtime_error("OAuth approval denied, consumed or expired");
    Credential credential{Secret(textField(root,"accessToken")),parseUtc(textField(root,"credentialExpiresAt")),std::string(installationId)};
    validateCredential(credential); return credential;
}
void AuthClient::cancel(const AuthSession& session, std::stop_token stop) {
    if (!validGuid(session.id) || !opaqueSecret(session.claim.view())) throw std::runtime_error("Invalid OAuth cancellation identity");
    Secret header("LSOAuthClaim ",session.claim.view());
    (void)http_.request(L"DELETE",L"/api/v1/auth/discord/sessions/"+widen(session.id),{},header.view(),stop);
}
CredentialStore::CredentialStore(Endpoint endpoint, std::filesystem::path root) : endpoint_(std::move(endpoint)), root_(std::move(root)) {
    if (root_.empty()) {
        PWSTR folder = nullptr;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData,KF_FLAG_DEFAULT,nullptr,&folder))) throw std::runtime_error("Core local state folder unavailable");
        try { root_ = std::filesystem::path(folder) / L"LSOverlayCore"; } catch (...) { CoTaskMemFree(folder); throw; }
        CoTaskMemFree(folder);
    }
    root_ = std::filesystem::absolute(root_);
    path_ = root_ / (widen(sha256(asBytes(endpoint_.origin))) + L".credential");
}
std::string CredentialStore::installationId() {
    std::filesystem::create_directories(root_);
    const auto filePath = root_ / L"installation-id.txt";
    FileHandle file{CreateFileW(filePath.c_str(),GENERIC_READ | GENERIC_WRITE,FILE_SHARE_READ,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr)};
    if (file.value == INVALID_HANDLE_VALUE) throw std::runtime_error("Core installation identity is in use or inaccessible");
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.value,&size)) throw std::runtime_error("Cannot read Core identity size");
    if (size.QuadPart == 0) {
        const auto id = newInstallationId(); DWORD written = 0;
        if (!WriteFile(file.value,id.data(),static_cast<DWORD>(id.size()),&written,nullptr) || written != id.size() || !FlushFileBuffers(file.value))
            throw std::runtime_error("Cannot persist Core identity");
        return id;
    }
    if (size.QuadPart != 36) throw std::runtime_error("Corrupt Core identity file");
    std::string id(36,'\0'); DWORD read = 0;
    if (!ReadFile(file.value,id.data(),36,&read,nullptr) || read != 36 || !validGuid(id)) throw std::runtime_error("Corrupt Core identity");
    return id;
}
void CredentialStore::save(const Credential& credential) {
    validateCredential(credential);
    std::filesystem::create_directories(root_);
    Secret plaintext("{\"origin\":" + jsonQuote(endpoint_.origin) + ",\"installationId\":" + jsonQuote(credential.installationId) +
        ",\"expiresTicks\":" + std::to_string(credential.expiresTicks) + ",\"token\":" + jsonQuote(credential.token.view()) + "}");
    const std::string entropyText = "LSOverlayCore.NativeCredential.v1|" + endpoint_.origin;
    DATA_BLOB input{static_cast<DWORD>(plaintext.view().size()),reinterpret_cast<BYTE*>(const_cast<char*>(plaintext.view().data()))};
    DATA_BLOB entropy{static_cast<DWORD>(entropyText.size()),reinterpret_cast<BYTE*>(const_cast<char*>(entropyText.data()))};
    ProtectedBlob encrypted;
    if (!CryptProtectData(&input,L"LS Overlay Core",&entropy,nullptr,nullptr,CRYPTPROTECT_UI_FORBIDDEN,&encrypted.value))
        throw std::runtime_error("Current-user credential protection failed");
    atomicEncryptedWrite(path_,encrypted.value);
}
std::optional<Credential> CredentialStore::load() const {
    FileHandle file{CreateFileW(path_.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr)};
    if (file.value == INVALID_HANDLE_VALUE) {
        if (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND) return std::nullopt;
        throw std::runtime_error("Core credential file inaccessible");
    }
    LARGE_INTEGER length{};
    if (!GetFileSizeEx(file.value,&length) || length.QuadPart <= 0 || length.QuadPart > 16384) throw std::runtime_error("Invalid Core credential size");
    std::vector<BYTE> bytes(static_cast<std::size_t>(length.QuadPart)); DWORD read = 0;
    if (!ReadFile(file.value,bytes.data(),static_cast<DWORD>(bytes.size()),&read,nullptr) || read != bytes.size()) throw std::runtime_error("Core credential read failed");
    const std::string entropyText = "LSOverlayCore.NativeCredential.v1|" + endpoint_.origin;
    DATA_BLOB input{static_cast<DWORD>(bytes.size()),bytes.data()};
    DATA_BLOB entropy{static_cast<DWORD>(entropyText.size()),reinterpret_cast<BYTE*>(const_cast<char*>(entropyText.data()))};
    ProtectedBlob clear;
    if (!CryptUnprotectData(&input,nullptr,&entropy,nullptr,nullptr,CRYPTPROTECT_UI_FORBIDDEN,&clear.value)) throw std::runtime_error("Core credential user/origin binding mismatch or corruption");
    Json json(std::string_view(reinterpret_cast<char*>(clear.value.pbData),clear.value.cbData),16384);
    auto* root = json.root();
    if (textField(root,"origin") != endpoint_.origin) throw std::runtime_error("Core credential origin mismatch");
    Credential credential{Secret(textField(root,"token")),static_cast<std::int64_t>(numberField(root,"expiresTicks")),std::string(textField(root,"installationId"))};
    validateCredential(credential); return credential;
}
}
