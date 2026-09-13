#pragma once
#include "transport.hpp"
#include "json.hpp"
#include <optional>

namespace core {
struct AuthSession {
    std::string id;
    Secret claim;
    std::string authorizationUrl;
    std::int64_t expiresTicks;
};
struct Credential {
    Secret token;
    std::int64_t expiresTicks;
    std::string installationId;
};
void validateAuthSession(const AuthSession& session, const Endpoint& endpoint);
std::string newInstallationId();
bool validGuid(std::string_view value);

class AuthClient final {
public:
    explicit AuthClient(HttpTransport& http) : http_(http) {}
    AuthSession start(std::string_view installationId, std::stop_token stop = {});
    // Pending returns null; denial/expiry/error never returns a usable token.
    std::optional<Credential> poll(const AuthSession& session, std::string_view installationId, std::stop_token stop = {});
    void cancel(const AuthSession& session, std::stop_token stop = {});
private:
    HttpTransport& http_;
};

class CredentialStore final {
public:
    explicit CredentialStore(Endpoint endpoint, std::filesystem::path root = {});
    std::string installationId();
    void save(const Credential& credential);
    std::optional<Credential> load() const;
    const std::filesystem::path& path() const noexcept { return path_; }
private:
    Endpoint endpoint_;
    std::filesystem::path root_, path_;
};
}
