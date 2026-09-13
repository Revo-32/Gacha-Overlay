#pragma once
#include "core_wire.hpp"
#include "transport.hpp"
#include <optional>

namespace core {
struct SalesCommand {
    std::string message, generation, requestId;
    bool undo = false;
    bool operator==(const SalesCommand&) const = default;
};
std::string salesCommandBody(const SalesCommand& command);
struct SalesActionMetrics {unsigned submitted=0, completed=0, undone=0, rejected=0, uncertain=0;};

// One in-flight user intent. No timer retries, optimistic queue edits or tokens
// in this object. HTTP acceptance and canonical read-back are separate gates.
class SalesActions final {
public:
    void observe(const Json& snapshot);
    void disconnected();
    std::optional<SalesCommand> offered() const;
    bool submit(const SalesCommand& offer);
    std::optional<SalesCommand> wait(std::stop_token stop);
    void response(const SalesCommand& command, unsigned status, std::string_view body);
    void uncertain(const SalesCommand& command);
    bool busy() const;
    std::wstring status() const;
    SalesActionMetrics metrics() const;
private:
    mutable std::mutex mutex_;
    std::condition_variable_any changed_;
    std::optional<SalesCommand> offer_, pending_, queued_;
    bool accepted_ = false, observed_ = false;
    std::wstring status_;
    SalesActionMetrics metrics_;
    void finish();
};
void runSalesActions(SalesActions& actions, std::string_view token, std::stop_token stop,
    const std::function<void()>& notify);
}
