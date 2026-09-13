#include "dev_client.hpp"
#include "auth.hpp"
#include "core_connection.hpp"
#include <shellapi.h>
#include <algorithm>

namespace core {
void runDevelopmentClient(std::wstring origin,std::stop_token stop,const std::function<void(std::wstring,std::shared_ptr<const Json>)>& publish,
    const std::function<void(std::string_view)>& mediaReady,ChannelControl& channels,SalesActions* actions,const std::function<void()>& actionChanged) {
    struct Cleanup {SalesActions* actions;const std::function<void()>& notify;~Cleanup(){if(actions){actions->disconnected();if(notify)notify();}}} cleanup{actions,actionChanged};
    const auto status=[&](std::wstring text){publish(std::move(text),{});};
    const auto clear=[&](std::wstring text){
        publish(std::move(text),std::make_shared<Json>(R"({"protocolVersion":1,"generation":"disconnected","revision":1,"selfUserId":"0","chat":[],"sales":{"revision":0,"observationStatus":"Unavailable","isTrackingEnabled":false,"queue":[],"waitingCount":0,"currentIsSelf":false,"nextIsSelf":false,"containsUnverifiedItems":false},"session":[],"chatConnectionState":"unavailable","chatSelection":{"slot":-1,"name":"연결 대기","availableSlots":[]}})"));
    };
    std::mutex mutex;std::condition_variable_any wake;
    const auto pause=[&](std::chrono::milliseconds duration){std::unique_lock lock(mutex);wake.wait_for(lock,stop,duration,[]{return false;});};
    try {
        Endpoint endpoint(origin);
        const bool production=endpoint.secure && endpoint.host==L"overlay.revo32.cloud" && endpoint.port==443;
        if(!production && (!endpoint.loopback || endpoint.secure || endpoint.host!=L"127.0.0.1" || endpoint.port!=15190))
            throw std::runtime_error("Unsupported Core endpoint");
        HttpTransport bridge(endpoint);
        bool mediaEnabled=false;
        while(!stop.stop_requested()) {
            try {
                auto response=bridge.request(L"GET",production?L"/api/v1/core/manifest":L"/core-dev/manifest",{},{},stop);
                if(response.status==404 || response.status==426){status(L"Core 서비스 준비 또는 앱 업데이트가 필요합니다");return;}
                if(response.status!=200)throw TransportError("Core manifest unavailable",response.status);
                Json manifest(response.body,4096);
                if(textField(manifest.root(),"authOrigin")!="https://overlay.revo32.cloud" ||
                    (production ? !yyjson_is_false(field(manifest.root(),"readOnly")) : !yyjson_is_true(field(manifest.root(),"readOnly"))))
                    throw std::runtime_error("Unrecognized Core manifest");
                mediaEnabled=yyjson_is_true(field(manifest.root(),"mediaDelivery"));break;
            }catch(const TransportError&){if(stop.stop_requested())return;status(L"Core 서버 재연결 중");pause(std::chrono::seconds(5));}
        }
        if(stop.stop_requested())return;
        // OAuth and Sales commands use only the fixed HTTPS issuer, never an arbitrary bridge.
        Endpoint issuer(L"https://overlay.revo32.cloud");HttpTransport authHttp(issuer);AuthClient auth(authHttp);CredentialStore store(issuer);
        auto credential=store.load();
        std::optional<SnapshotDocument> current;unsigned failures=0;
        while(!stop.stop_requested()) {
            const auto started=std::chrono::steady_clock::now();
            try {
                if(!credential || credential->expiresTicks<=utcTicks()) {
                    credential.reset();current.reset();clear(L"브라우저에서 Discord 로그인 승인 대기");
                    const auto installation=store.installationId();auto session=auth.start(installation,stop);
                    const auto url=widen(session.authorizationUrl);
                    if(reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr,L"open",url.c_str(),nullptr,nullptr,SW_SHOWNORMAL))<=32)
                        throw std::runtime_error("Browser launch failed");
                    try {while(!stop.stop_requested() && !credential){pause(std::chrono::seconds(2));if(!stop.stop_requested())credential=auth.poll(session,installation,stop);}}
                    catch(...){try{auth.cancel(session,stop);}catch(...){}if(!stop.stop_requested())clear(L"로그인 승인 실패 또는 만료 · 앱을 다시 실행해 주세요");return;}
                    if(!credential){try{auth.cancel(session);}catch(...){}return;}
                    store.save(*credential);
                }
                if(mediaEnabled)mediaReady(credential->token.view());
                std::jthread actionWorker;
                if(actions)actionWorker=std::jthread([&](std::stop_token workerStop){
                    std::stop_source combined;std::stop_callback outer(stop,[&]{combined.request_stop();});std::stop_callback inner(workerStop,[&]{combined.request_stop();});
                    runSalesActions(*actions,credential->token.view(),combined.get_token(),actionChanged);
                });
                CoreSession connection(bridge,credential->token.view(),stop);
                std::atomic<bool> catalogReady=false;
                const auto show=[&]{
                    if(actions)actions->observe(*current->json);
                    const auto phase=textField(current->json->root(),"chatConnectionState");
                    if(phase=="live" && !catalogReady.exchange(true))channels.restore();
                    publish(phase=="live"?(production?L"실시간 채팅 · 판매 · 세션 연결됨":L"개발 실시간 채팅 · 연결됨"):
                        phase=="unavailable"?L"채팅 권한/채널 확인 필요 · 제한된 내용은 제거됨":L"채팅 연결 복구 중",current->json);
                };
                connection.synchronize(current,stop);show();
                std::jthread selection([&](std::stop_token requestedStop){
                    std::stop_source combined;std::stop_callback outer(stop,[&]{combined.request_stop();});std::stop_callback inner(requestedStop,[&]{combined.request_stop();});
                    while(!combined.stop_requested() && !catalogReady.load())pause(std::chrono::milliseconds(100));
                    while(auto slot=channels.wait(combined.get_token())) {
                        try {HttpTransport control(endpoint);Secret bearer("Bearer ",credential->token.view());
                            auto selected=control.request(L"POST",(production?L"/api/v1/core/channel/":L"/core-dev/channel/")+std::to_wstring(*slot),"{}",bearer.view(),combined.get_token());
                            if(selected.status!=200)status(L"채팅방 변경 실패 · 접근 권한과 연결 상태를 확인해 주세요");
                        }catch(const std::exception&){if(!combined.stop_requested())status(L"채팅방 변경 실패 · 기존 표시를 유지합니다");}
                    }
                });
                while(!stop.stop_requested()) {
                    if(credential->expiresTicks<=utcTicks())throw TransportError("Credential expired",401);
                    if(connection.receiveUpdate(current,stop))show();
                }
            }catch(const TransportError& error) {
                if(actions){actions->disconnected();if(actionChanged)actionChanged();}
                if(stop.stop_requested())return;
                current.reset();clear(L"Core 서버 재연결 중");
                if(error.httpStatus==401){credential.reset();continue;}
                if(error.httpStatus==403 || error.httpStatus==404 || error.httpStatus==426){clear(L"Core 접근 권한 또는 앱 업데이트 확인이 필요합니다");return;}
                if(std::chrono::steady_clock::now()-started>=std::chrono::seconds(30))failures=0;
                pause(std::chrono::milliseconds(std::min(30000U,1000U<<std::min(failures++,5U))));
            }catch(const std::exception&){
                if(actions){actions->disconnected();if(actionChanged)actionChanged();}
                if(stop.stop_requested())return;current.reset();clear(L"Core 연결 복구 중 · 잠시 후 다시 연결합니다");pause(std::chrono::seconds(5));
            }
        }
    }catch(const std::exception&){if(!stop.stop_requested())clear(L"Core 연결을 시작할 수 없습니다. 앱을 다시 실행해 주세요.");}
}
}
