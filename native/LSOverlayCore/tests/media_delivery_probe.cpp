#include "transport.hpp"
#include "media_package.hpp"
#include "media_store.hpp"
#include <iostream>
#include <chrono>
#include <fstream>
int wmain(int argc,wchar_t** argv) {
    unsigned checks=0;
    const auto check=[&](bool value) {++checks;if (!value) throw std::runtime_error("Media transport assertion failed");};
    try {
        if (argc!=3) throw std::runtime_error("Explicit synthetic loopback and private artifact path required");
        core::Endpoint endpoint(argv[1]);
        if (!endpoint.loopback || endpoint.secure || endpoint.port==15190) throw std::runtime_error("Synthetic media probe cannot use the live bridge");
        const auto directory=std::filesystem::absolute(argv[2]);core::validateMediaPath(directory);std::filesystem::create_directories(directory);
        core::HttpTransport http(endpoint);const std::string authorization="Bearer lso_"+std::string(43,'a'); // fixture-only sentinel
        const auto id=[](unsigned n) {return std::string(47,'a')+"0123456789abcdef"[n];};
        const auto good=directory/L"good.lscm";
        const auto result=http.downloadMedia(id(0),32,32,authorization,good);
        {core::MediaPackage package(good);check(package.width()==result.width && package.height()==result.height);check(package.count()>0);}
        check(result.bytes==std::filesystem::file_size(good));check(result.cacheHit);
        for (unsigned scenario=1;scenario<=9;++scenario) {
            const auto path=directory/(std::to_wstring(scenario)+L".lscm");bool rejected=false;
            try {(void)http.downloadMedia(id(scenario),32,32,authorization,path);}catch(const std::exception&) {rejected=true;}
            check(rejected);check(!std::filesystem::exists(path));
        }
        bool existingRejected=false;
        try {(void)http.downloadMedia(id(0),32,32,authorization,good);}catch(const std::exception&) {existingRejected=true;}
        check(existingRejected && std::filesystem::file_size(good)==result.bytes);
        std::stop_source stop;const auto start=std::chrono::steady_clock::now();
        std::jthread cancel([&] {std::this_thread::sleep_for(std::chrono::milliseconds(200));stop.request_stop();});
        bool cancelled=false;const auto cancelledPath=directory/L"cancelled.lscm";
        try {(void)http.downloadMedia(id(10),32,32,authorization,cancelledPath,stop.get_token());}catch(const std::exception&) {cancelled=true;}
        check(cancelled && std::chrono::steady_clock::now()-start<std::chrono::seconds(3));check(!std::filesystem::exists(cancelledPath));
        for (const auto& invalid : {"", "../private", "https://evil.test/x", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}) check(!core::validMediaIdentity(invalid));
        check(core::validMediaIdentity(std::string(48,'a')));
        bool badProfile=false;try {core::validateMediaProfile(8192,8192);}catch(const std::exception&) {badProfile=true;}check(badProfile);
        {
            std::atomic_uint notifications=0;core::MediaStore store([&] {++notifications;});
            store.setFetcher(core::makeMediaFetcher(endpoint,"lso_"+std::string(43,'a')));
            const auto waitFor=[&](auto predicate) {const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(3);while (!predicate() && std::chrono::steady_clock::now()<deadline) std::this_thread::sleep_for(std::chrono::milliseconds(10));check(predicate());};
            auto alias=id(0);alias.front()='b';
            check(store.prepare(id(0),32,32));check(store.prepare(alias,32,32));
            check(!store.ready(id(0)));
            store.setVisible({id(0),alias});waitFor([&] {return store.frame(id(0)) && store.frame(alias);});
            check(store.ready(id(0)) && store.layoutRevision()>0);
            check(store.sourceKey(id(0))==store.sourceKey(alias));check(store.statistics().visible==1);
            check(store.statistics().ownedPixelBytes==static_cast<std::uint64_t>(result.width)*result.height*4);
            store.retain({alias});store.setVisible({});waitFor([&] {return store.statistics().ownedPixelBytes==0;});
            check(store.ready(alias)); // hiding keeps successful-preview URL policy stable
            store.setVisible({alias});waitFor([&] {return store.frame(alias)!=nullptr;});
            check(store.prepare(id(9),32,32));store.setVisible({alias,id(9)});
            waitFor([&] {return store.failed(id(9));});check(store.frame(alias)!=nullptr);
            check(!store.ready(id(9))); // failed preview must keep its original link
            store.retain({});store.setVisible({});waitFor([&] {return store.statistics().visible==0;});
            check(notifications>0);
        }
        {
            const auto bytes=result.bytes;
            auto fetch=core::makeMediaFetcher(endpoint,"lso_"+std::string(43,'a'),{bytes*2,bytes*2-1,bytes,16});
            auto firstId=id(0),secondId=id(0),thirdId=id(0);firstId[0]='c';secondId[0]='d';thirdId[0]='e';
            auto first=fetch(firstId,32,32,{}),second=fetch(secondId,32,32,{});
            const auto firstPath=first.file->path;
            bool busy=false;try {(void)fetch(thirdId,32,32,{});}catch(const core::TransportError& error) {busy=error.httpStatus==429;}
            check(busy);check(std::filesystem::exists(firstPath));
            first.file.reset();auto third=fetch(thirdId,32,32,{});
            check(!std::filesystem::exists(firstPath));check(third.file && second.file);
            auto again=fetch(thirdId,32,32,{});check(again.file==third.file);
        }
        {
            auto fetch=core::makeMediaFetcher(endpoint,"lso_"+std::string(43,'a'));
            std::atomic_bool slowStarted=false,releaseSlow=false;
            core::MediaStore store([] {});
            const auto slow=id(0);auto fast=slow;fast[0]='b';
            store.setFetcher([&](const auto& mediaId,unsigned width,unsigned height,std::stop_token token) {
                if (mediaId==slow) {
                    slowStarted=true;
                    const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(2);
                    while (!releaseSlow && !token.stop_requested() && std::chrono::steady_clock::now()<deadline) std::this_thread::sleep_for(std::chrono::milliseconds(5));
                }
                return fetch(mediaId,width,height,token);
            });
            check(store.prepare(slow,32,32));store.setVisible({slow});
            const auto started=std::chrono::steady_clock::now();
            while (!slowStarted && std::chrono::steady_clock::now()-started<std::chrono::seconds(1)) std::this_thread::sleep_for(std::chrono::milliseconds(5));
            check(slowStarted);check(store.prepare(fast,32,32));store.setVisible({slow,fast});
            const auto waiting=std::chrono::steady_clock::now();
            while (!store.frame(fast) && std::chrono::steady_clock::now()-waiting<std::chrono::seconds(1)) std::this_thread::sleep_for(std::chrono::milliseconds(5));
            const bool independent=store.frame(fast)!=nullptr && !store.frame(slow);
            releaseSlow=true;check(independent);
        }
        std::cout<<"PASS: "<<checks<<" synthetic native media delivery assertions; no live credentials\n";return 0;
    }catch(const std::exception& error) {std::cerr<<"FAIL: "<<checks<<": "<<error.what()<<'\n';return 1;}
}
