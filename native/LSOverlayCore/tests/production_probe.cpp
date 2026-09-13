#include "auth.hpp"
#include "core_connection.hpp"
#include <iostream>
#include <chrono>
#include <set>

// Operator-only read probe. Not shipped with the app. It reads only Core's own
// DPAPI credential, never Full files, and cannot dispatch a Sales/Discord write.
int wmain(int argc,wchar_t** argv) {
    try {
        if(argc!=2 || std::wstring_view(argv[1])!=L"--operator-readonly")return 2;
        core::Endpoint endpoint(L"https://overlay.revo32.cloud");core::HttpTransport http(endpoint);core::CredentialStore store(endpoint);
        auto credential=store.load();if(!credential || credential->expiresTicks<=core::utcTicks())throw std::runtime_error("Core login required");
        auto response=http.request(L"GET",L"/api/v1/core/manifest");core::Json manifest(response.body);
        if(response.status!=200 || !yyjson_is_false(core::field(manifest.root(),"readOnly")))throw std::runtime_error("Core manifest unavailable");
        std::optional<core::SnapshotDocument> current;std::string firstGeneration,viewer;
        for(unsigned cycle=0;cycle<2;++cycle) {
            const auto started=std::chrono::steady_clock::now();
            core::CoreSession session(http,credential->token.view());session.synchronize(current);
            for(unsigned frames=0;frames<100;++frames) {
                auto* root=current->json->root();auto* sales=core::field(root,"sales");
                if(core::textField(root,"chatConnectionState")=="live" && yyjson_is_obj(core::field(sales,"actions")))break;
                if(std::chrono::steady_clock::now()-started>std::chrono::seconds(40))throw std::runtime_error("Core live readiness deadline");
                session.receiveUpdate(current);
            }
            auto* root=current->json->root();auto* sales=core::field(root,"sales");
            if(core::textField(root,"chatConnectionState")!="live" || !yyjson_is_obj(core::field(sales,"actions")))throw std::runtime_error("Core canonical state unavailable");
            if(cycle==0){firstGeneration=current->generation;viewer=core::textField(root,"selfUserId");}
            else if(current->generation==firstGeneration || core::textField(root,"selfUserId")!=viewer)throw std::runtime_error("Core reconnect isolation failure");
            std::cout<<"{\"cycle\":"<<cycle+1<<",\"chatLive\":true,\"salesLive\":true,\"chatMessages\":"<<yyjson_arr_size(core::field(root,"chat"))
                <<",\"salesRows\":"<<yyjson_arr_size(core::field(sales,"queue"))<<",\"hosts\":"<<yyjson_arr_size(core::field(root,"session"))
                <<",\"readyMs\":"<<std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-started).count()<<"}\n";
            // Read one authorized derivative in memory-budgeted temporary storage.
            if(cycle==0) {
                std::set<std::string> ids;
                const auto walk=[&](auto&& self,yyjson_val* value,unsigned depth)->void {
                    if(depth>32)throw std::runtime_error("Core depth limit");
                    if(yyjson_is_str(value)){const std::string id(core::stringValue(value));if(core::validMediaIdentity(id))ids.insert(id);}
                    else if(yyjson_is_arr(value)){std::size_t i=0,n=0;yyjson_val* item=nullptr;yyjson_arr_foreach(value,i,n,item)self(self,item,depth+1);}
                    else if(yyjson_is_obj(value)){std::size_t i=0,n=0;yyjson_val* key=nullptr;yyjson_val* item=nullptr;yyjson_obj_foreach(value,i,n,key,item)self(self,item,depth+1);}
                };
                walk(walk,root,0);
                if(!ids.empty()) {
                    const auto path=std::filesystem::temp_directory_path()/(L"LSCore-read-probe-"+core::widen(core::newInstallationId())+L".tmp");
                    struct Clean {std::filesystem::path path;~Clean(){std::error_code ignored;std::filesystem::remove(path,ignored);}} clean{path};
                    core::Secret bearer("Bearer ",credential->token.view());auto media=http.downloadMedia(*ids.begin(),64,64,bearer.view(),path);
                    std::cout<<"{\"mediaAuthorized\":true,\"bytes\":"<<media.bytes<<",\"width\":"<<media.width<<",\"height\":"<<media.height<<"}\n";
                }else std::cout<<"{\"mediaAuthorized\":null,\"reason\":\"no-visible-media\"}\n";
            }
        }
        std::cout<<"PASS: production Core initial/reconnect; no Discord writes, no credential output\n";return 0;
    }catch(const std::exception&){std::cerr<<"FAIL: production Core read validation; no secrets recorded\n";return 1;}
}
