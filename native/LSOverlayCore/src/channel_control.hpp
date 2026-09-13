#pragma once
#include <condition_variable>
#include <mutex>
#include <stop_token>
#include <optional>
namespace core {
class ChannelControl {
public:
    void select(unsigned slot) {if(slot>7)return;std::lock_guard lock(mutex_);last_=slot;pending_=slot;wake_.notify_all();}
    void restore() {std::lock_guard lock(mutex_);if(!pending_)pending_=last_;wake_.notify_all();}
    std::optional<unsigned> wait(std::stop_token stop) {std::unique_lock lock(mutex_);wake_.wait(lock,stop,[&]{return pending_.has_value();});if(stop.stop_requested())return {};auto result=pending_;pending_.reset();return result;}
private:
    std::mutex mutex_;std::condition_variable_any wake_;std::optional<unsigned> pending_,last_;
};
}
