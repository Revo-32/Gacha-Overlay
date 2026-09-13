#pragma once
#include "fixture_client.hpp"
#include "channel_control.hpp"
#include "sales_actions.hpp"
namespace core {
void runDevelopmentClient(std::wstring origin,std::stop_token stop,const std::function<void(std::wstring,std::shared_ptr<const Json>)>& publish,
    const std::function<void(std::string_view)>& mediaReady,ChannelControl& channels,SalesActions* actions=nullptr,const std::function<void()>& actionChanged={});
}
