#include "auth.hpp"
#include "core_wire.hpp"
#include "core_connection.hpp"
#include <chrono>
#include <iostream>
#include <thread>

namespace {
bool connect(core::HttpTransport& http, const core::Credential& credential, std::optional<core::SnapshotDocument>& current) {
    core::CoreSession session(http,credential.token.view());
    return session.synchronize(current);
}
}

int wmain(int argc, wchar_t** argv) {
    unsigned checks = 0;
    auto check = [&](bool value) { ++checks; if (!value) throw std::runtime_error("Native contract E2E assertion failed"); };
    auto rejects = [&](auto action) { bool failed = false; try { action(); } catch (const std::exception&) { failed = true; } check(failed); };
    try {
        if (argc != 2) throw std::runtime_error("Usage: LSOverlayCoreTransportProbe <loopback-fixture-origin>");
        core::Endpoint endpoint(argv[1]);
        if (!endpoint.loopback || endpoint.secure) throw std::runtime_error("Probe is restricted to the isolated loopback contract fixture");
        core::HttpTransport http(endpoint);
        auto manifestResponse = http.request(L"GET",L"/fixture/manifest");
        core::Json manifest(manifestResponse.body);
        check(manifestResponse.status == 200 && yyjson_is_true(core::field(manifest.root(),"syntheticAuthentication")) &&
            yyjson_is_false(core::field(manifest.root(),"liveDiscord")));
        core::AuthClient auth(http);
        const auto identity = core::newInstallationId();
        auto session = auth.start(identity);
        check(!auth.poll(session,identity));
        std::this_thread::sleep_for(std::chrono::seconds(2));
        auto credential = auth.poll(session,identity); check(credential.has_value());
        rejects([&] { (void)auth.poll(session,identity); }); // Single-use claim.
        std::optional<core::SnapshotDocument> current;
        check(!connect(http,*credential,current));
        check(current && core::textField(current->json->root(),"selfUserId") == "77");
        check(connect(http,*credential,current)); // Exact state resume, no redundant bootstrap.
        core::Secret bearer("Bearer ",credential->token.view());
        const auto previousRevision = current->revision;
        check(http.request(L"POST",L"/fixture/revision",{},bearer.view()).status == 200);
        check(!connect(http,*credential,current) && current->revision > previousRevision);
        const auto previousGeneration = current->generation;
        check(http.request(L"POST",L"/fixture/generation",{},bearer.view()).status == 200);
        check(!connect(http,*credential,current) && current->generation != previousGeneration);
        core::Credential invalid{core::Secret("lso_" + std::string(43,'z')),credential->expiresTicks,identity};
        rejects([&] { (void)connect(http,invalid,current); });
        rejects([&] { (void)http.request(L"GET",L"/fixture/redirect",{},bearer.view()); });
        rejects([&] { (void)http.request(L"GET",L"/fixture/oversized"); });
        std::stop_source stop;
        const auto started = std::chrono::steady_clock::now();
        std::jthread cancel([&] { std::this_thread::sleep_for(std::chrono::milliseconds(200)); stop.request_stop(); });
        rejects([&] { (void)http.request(L"GET",L"/fixture/delayed",{},{},stop.get_token()); });
        check(std::chrono::steady_clock::now()-started < std::chrono::seconds(3));
        auto cancelled = auth.start(identity); auth.cancel(cancelled);
        rejects([&] { (void)auth.poll(cancelled,identity); });
        {
            core::CoreSession stream(http,credential->token.view());
            check(stream.synchronize(current));
            check(!stream.receiveUpdate(current)); // Real heartbeat received and ACK sent.
            std::stop_source socketStop;
            const auto socketStart = std::chrono::steady_clock::now();
            std::jthread interrupt([&] { std::this_thread::sleep_for(std::chrono::milliseconds(200)); socketStop.request_stop(); });
            rejects([&] { (void)stream.receiveUpdate(current,socketStop.get_token()); });
            check(std::chrono::steady_clock::now()-socketStart < std::chrono::seconds(3));
        }
        std::cout << "PASS: " << checks << " native WinHTTP/auth/Core snapshot/reconnect assertions; synthetic fixture only\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << "FAIL: " << error.what() << '\n'; return 1; }
}
