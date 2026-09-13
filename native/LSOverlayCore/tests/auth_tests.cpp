#include "auth.hpp"
#include <fstream>
#include <iostream>
#include <iterator>

int wmain(int argc, wchar_t** argv) {
    unsigned checks = 0;
    auto check = [&](bool value) { ++checks; if (!value) throw std::runtime_error("Native auth assertion failed"); };
    auto rejects = [&](auto action) { bool rejected = false; try { action(); } catch (const std::exception&) { rejected = true; } check(rejected); };
    try {
        for (const auto* url : {L"https://overlay.revo32.cloud",L"http://127.0.0.1:15188",L"http://[::1]:15188",L"http://localhost:15188"}) {
            core::Endpoint endpoint(url); check(!endpoint.origin.empty());
        }
        for (const auto* url : {L"http://192.168.0.10:5188",L"http://evil.test",L"https://user:pass@evil.test",L"https://evil.test/path",L"https://evil.test/#token",L"https://evil.test/?key=value",L"file:///etc/passwd",L"http://127.0.0.1.evil.test"})
            rejects([&] { core::Endpoint endpoint(url); });
        check(core::Endpoint(L"https://OVERLAY.revo32.cloud:443/").origin == "https://overlay.revo32.cloud");
        check(core::parseUtc("2026-09-13T00:00:00Z") == core::parseUtc("2026-09-13T00:00:00.0000000+00:00"));
        for (const char* time : {"2026-02-30T00:00:00Z","2026-09-13T00:00:00","2026-09-13T00:00:00+09:00","2026-09-13T00:00:00.Z"})
            rejects([&] { (void)core::parseUtc(time); });
        check(core::narrow(core::widen("한국어🙂")) == "한국어🙂");
        const auto id = core::newInstallationId(); check(core::validGuid(id));
        core::Endpoint endpoint(L"http://127.0.0.1:15188");
        const std::string url = "https://discord.com/oauth2/authorize?client_id=123&response_type=code&scope=identify&redirect_uri=http%3A%2F%2F127.0.0.1%3A15188%2Fauth%2Fdiscord%2Fcallback&state=" + std::string(43,'s') + "&code_challenge_method=S256&code_challenge=" + std::string(43,'c');
        const auto expires = core::utcTicks()+60LL*10000000;
        core::AuthSession valid{id,core::Secret(std::string(43,'x')),url,expires};
        core::validateAuthSession(valid,endpoint); ++checks;
        for (auto invalid : {url + "&scope=identify", url + "#fragment", url + "&", std::string("https://evil.test/oauth2/authorize")}) {
            core::AuthSession session{id,core::Secret(std::string(43,'x')),invalid,expires};
            rejects([&] { core::validateAuthSession(session,endpoint); });
        }
        core::Endpoint other(L"https://overlay.revo32.cloud");
        rejects([&] { core::validateAuthSession(valid,other); });
        if (argc > 1) {
            // Explicit ignored artifact directory supplied by the test runner.
            const auto root = std::filesystem::absolute(argv[1]) / core::widen(core::newInstallationId());
            core::CredentialStore store(endpoint,root);
            check(!store.load());
            const auto installation = store.installationId(); check(store.installationId() == installation);
            core::Credential credential{core::Secret("lso_" + std::string(43,'q')),expires,installation};
            store.save(credential);
            auto loaded = store.load(); check(loaded && loaded->token.view() == credential.token.view());
            std::ifstream file(store.path(),std::ios::binary);
            std::string ciphertext((std::istreambuf_iterator<char>(file)),std::istreambuf_iterator<char>()); file.close();
            check(ciphertext.find(std::string(credential.token.view())) == std::string::npos);
            core::CredentialStore wrongOrigin(other,root);
            std::filesystem::copy_file(store.path(),wrongOrigin.path());
            rejects([&] { (void)wrongOrigin.load(); });
            ciphertext.back() ^= 0x55;
            { std::ofstream corrupt(store.path(),std::ios::binary); corrupt.write(ciphertext.data(),static_cast<std::streamsize>(ciphertext.size())); }
            rejects([&] { (void)store.load(); });
            // Known test-created files only, never a recursive cleanup.
            std::filesystem::remove(store.path()); std::filesystem::remove(wrongOrigin.path());
            std::filesystem::remove(root / L"installation-id.txt"); std::filesystem::remove(root);
        }
        std::cout << checks << " native endpoint/OAuth/credential assertions passed\n"; return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
