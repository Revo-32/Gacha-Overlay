#include "fixture_client.hpp"
#include "auth.hpp"
#include "core_connection.hpp"
#include <algorithm>

namespace core {
void runFixtureClient(std::wstring origin, std::stop_token stop, const std::function<void(std::wstring,std::shared_ptr<const Json>)>& publish) {
    auto status = [&](std::wstring text) { publish(std::move(text),{}); };
    std::mutex mutex; std::condition_variable_any wake;
    auto pause = [&](std::chrono::milliseconds duration) {
        std::unique_lock lock(mutex); wake.wait_for(lock,stop,duration,[] { return false; });
    };
    try {
        Endpoint endpoint(origin);
        if (!endpoint.loopback || endpoint.secure) throw std::runtime_error("Fixture mode requires explicit HTTP loopback");
        HttpTransport http(endpoint);
        auto response = http.request(L"GET",L"/fixture/manifest",{},{},stop);
        Json manifest(response.body,65536);
        if (response.status != 200 || !yyjson_is_true(field(manifest.root(),"syntheticAuthentication")) || !yyjson_is_false(field(manifest.root(),"liveDiscord")))
            throw std::runtime_error("Not an isolated synthetic contract fixture");
        status(L"합성 환경 · 인증 계약 확인 중 (실제 Discord 아님)");
        AuthClient auth(http); const auto identity = newInstallationId();
        auto session = auth.start(identity,stop);
        auto credential = auth.poll(session,identity,stop);
        while (!credential && !stop.stop_requested()) { pause(std::chrono::seconds(2)); credential = auth.poll(session,identity,stop); }
        if (!credential || stop.stop_requested()) return;
        std::optional<SnapshotDocument> current;
        unsigned failures = 0;
        auto summary = [&] {
            auto* root = current->json->root();
            publish(L"M2 합성 연결 정상 · 채팅 " + std::to_wstring(yyjson_arr_size(field(root,"chat"))) +
                L" / 판매 " + std::to_wstring(yyjson_arr_size(field(field(root,"sales"),"queue"))) +
                L" / 세션 " + std::to_wstring(yyjson_arr_size(field(root,"session"))),current->json);
        };
        while (!stop.stop_requested()) {
            const auto connectedAt = std::chrono::steady_clock::now();
            try {
                if (credential->expiresTicks <= utcTicks()) { status(L"검증 인증 만료 · 다시 실행해 주세요."); return; }
                CoreSession connection(http,credential->token.view(),stop);
                (void)connection.synchronize(current,stop); summary();
                while (!stop.stop_requested()) if (connection.receiveUpdate(current,stop)) summary();
            } catch (const TransportError& error) {
                if (stop.stop_requested()) return;
                if (error.httpStatus == 401 || error.httpStatus == 403 || error.httpStatus == 404 || error.httpStatus == 426) {
                    status(L"검증 인증/기능 확인 필요 · 자동 재시도 중단"); return;
                }
                if (std::chrono::steady_clock::now()-connectedAt >= std::chrono::seconds(30)) failures = 0;
                const auto delay = std::min<unsigned>(15000,500U << std::min<unsigned>(failures++,5)) + static_cast<unsigned>(GetTickCount64()%251);
                status(L"합성 연결 복구 대기 · 마지막 정상 상태 유지");
                pause(std::chrono::milliseconds(delay));
            }
        }
    } catch (const std::exception&) {
        if (!stop.stop_requested()) status(L"연결 검증 실패 · 안전하지 않은 응답은 적용하지 않습니다.");
    }
}
}
