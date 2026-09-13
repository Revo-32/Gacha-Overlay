#pragma once
#include "core_wire.hpp"
#include "transport.hpp"

namespace core {
enum class CoreFrameResult { pending, heartbeat, ready, resumed };
class CoreStreamState final {
public:
    using Clock = std::chrono::steady_clock;
    explicit CoreStreamState(Clock::time_point started = Clock::now()) : transactionStarted_(started) {}
    CoreFrameResult accept(std::string_view frame, std::optional<SnapshotDocument>& current, Clock::time_point now = Clock::now());
private:
    SnapshotAssembler assembler_;
    std::optional<SnapshotDocument> pending_;
    std::optional<Clock::time_point> transactionStarted_;
};
class CoreSession final {
public:
    CoreSession(HttpTransport& http, std::string_view token, std::stop_token stop = {}) : socket_(http,token,stop) {}
    bool synchronize(std::optional<SnapshotDocument>& current, std::stop_token stop = {});
    bool receiveUpdate(std::optional<SnapshotDocument>& current, std::stop_token stop = {});
private:
    WebSocket socket_;
    CoreStreamState state_;
    bool lastResumed_ = false;
};
}
