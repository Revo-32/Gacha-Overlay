#pragma once
#include "fixture_client.hpp"
namespace core {
void runDevelopmentClient(std::wstring origin,std::stop_token stop,const std::function<void(std::wstring,std::shared_ptr<const Json>)>& publish);
}
