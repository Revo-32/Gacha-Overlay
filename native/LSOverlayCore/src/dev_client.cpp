#include "dev_client.hpp"
#include "auth.hpp"
#include "core_connection.hpp"
#include <shellapi.h>
#include <algorithm>

namespace core {
void runDevelopmentClient(std::wstring origin,std::stop_token stop,const std::function<void(std::wstring,std::shared_ptr<const Json>)>& publish) {
    const auto status = [&](std::wstring text) { publish(std::move(text),{}); };
    std::mutex mutex; std::condition_variable_any wake;
    const auto pause = [&](std::chrono::milliseconds duration) { std::unique_lock lock(mutex); wake.wait_for(lock,stop,duration,[] { return false; }); };
    std::wstring step=L"개발 경로 확인";
    try {
        Endpoint endpoint(origin);
        if (!endpoint.loopback || endpoint.secure || endpoint.host != L"127.0.0.1" || endpoint.port != 15190) throw std::runtime_error("Dev bridge requires the explicit SSH loopback port");
        HttpTransport bridge(endpoint);
        auto response=bridge.request(L"GET",L"/core-dev/manifest",{},{},stop); Json manifest(response.body,4096);
        if (response.status!=200 || !yyjson_is_true(field(manifest.root(),"readOnly")) || textField(manifest.root(),"authOrigin")!="https://overlay.revo32.cloud")
            throw std::runtime_error("Unrecognized read-only development bridge");
        // Authentication happens directly at the existing issuer. No forwarding
        // of OAuth claims/secrets through the dev bridge and no Full files read.
        Endpoint issuer(L"https://overlay.revo32.cloud"); HttpTransport authHttp(issuer); AuthClient auth(authHttp); CredentialStore store(issuer);
        step=L"Core 전용 자격 증명 확인";
        auto credential=store.load();
        if (!credential || credential->expiresTicks <= utcTicks()) {
            step=L"로그인 세션 생성";
            const auto installation=store.installationId(); auto session=auth.start(installation,stop);
            status(L"개발 연결 · 브라우저에서 Discord 로그인 승인 대기");
            const auto url=widen(session.authorizationUrl);
            step=L"로그인 브라우저 열기";
            if (reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr,L"open",url.c_str(),nullptr,nullptr,SW_SHOWNORMAL))<=32) throw std::runtime_error("Browser launch failed");
            try {
                step=L"로그인 승인 확인";
                while (!stop.stop_requested() && !credential) { pause(std::chrono::seconds(2)); if (!stop.stop_requested()) credential=auth.poll(session,installation,stop); }
            } catch (...) { try { auth.cancel(session,stop); } catch (...) {} throw; }
            if (!credential) { try { auth.cancel(session); } catch (...) {} return; }
            store.save(*credential);
        }
        step=L"인증된 Core stream 확인";
        std::optional<SnapshotDocument> current; unsigned failures=0;
        while (!stop.stop_requested()) {
            const auto started=std::chrono::steady_clock::now();
            try {
                if (credential->expiresTicks<=utcTicks()) { status(L"Core 인증 만료 · 로그인 재승인 필요"); return; }
                CoreSession connection(bridge,credential->token.view(),stop);
                const auto show = [&] {
                    const auto phase=textField(current->json->root(),"chatConnectionState");
                    publish(phase=="live" ? L"개발 실시간 채팅 · 판매 읽기 전용 · 미디어 연결 준비 중" :
                        phase=="unavailable" ? L"채팅 권한/채널 확인 필요 · 제한된 내용은 제거됨" : L"채팅 연결 복구 중 · 마지막 표시와 실시간 상태를 구분합니다",current->json);
                };
                connection.synchronize(current,stop); show();
                while (!stop.stop_requested()) if (connection.receiveUpdate(current,stop)) show();
            } catch (const TransportError& error) {
                if (stop.stop_requested()) return;
                if (error.httpStatus==401 || error.httpStatus==403 || error.httpStatus==404 || error.httpStatus==426) { status(L"Core 인증/개발 경로 확인 필요 · 자동 재시도 중단"); return; }
                if (std::chrono::steady_clock::now()-started>=std::chrono::seconds(30)) failures=0;
                status(L"개발 서버 재연결 중 · 마지막 표시 유지 (실시간 아님)");
                pause(std::chrono::milliseconds(std::min(30000U,1000U<<std::min(failures++,5U))));
            }
        }
    } catch (const std::exception&) { if (!stop.stop_requested()) status(L"개발 연결 중단 · "+step+L" 실패 (비밀값 기록 없음)"); }
}
}
