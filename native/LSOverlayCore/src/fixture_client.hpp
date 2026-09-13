#pragma once
#include <functional>
#include <stop_token>
#include <string>
#include "json.hpp"

namespace core {
// Explicit synthetic-only UI prototype. Real auth classes are separate and do
// not contain an auto-approval shortcut or fixture fallback.
void runFixtureClient(std::wstring origin, std::stop_token stop,
    const std::function<void(std::wstring,std::shared_ptr<const Json>)>& publish);
}
