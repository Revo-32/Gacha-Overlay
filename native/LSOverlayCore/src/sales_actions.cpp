#include "sales_actions.hpp"
#include <objbase.h>
#include <charconv>

namespace core {
namespace {
bool identifier(std::string_view value) {
    std::uint64_t id = 0;
    const auto result = std::from_chars(value.data(),value.data()+value.size(),id);
    return !value.empty() && value.front() != '0' && result.ec == std::errc{} && result.ptr == value.data()+value.size() && id != 0;
}
std::string newRequestId() {
    GUID id{}; wchar_t text[40]{};
    if (FAILED(CoCreateGuid(&id)) || !StringFromGUID2(id,text,40)) throw std::runtime_error("Request identity unavailable");
    auto result=narrow(std::wstring_view(text+1,36));
    // StringFromGUID2 emits uppercase, while System.Text.Json writes Guid in
    // lowercase. Use its canonical spelling so a valid response is not lost.
    for(auto& character:result)if(character>='A' && character<='F')character=static_cast<char>(character-'A'+'a');
    return result;
}
bool same(const SalesCommand& a,const SalesCommand& b) { return a.message==b.message && a.generation==b.generation && a.undo==b.undo; }
}
std::string salesCommandBody(const SalesCommand& command) {
    if (!identifier(command.message) || command.generation.empty() || command.generation.size()>128 || command.requestId.size()!=36)
        throw std::runtime_error("Invalid Sales intent");
    return "{\"protocolVersion\":1,\"messageId\":"+command.message+",\"desiredStatus\":"+jsonQuote(command.undo?"clear":"completed")+
        ",\"clientRequestId\":"+jsonQuote(command.requestId)+",\"salesGeneration\":"+jsonQuote(command.generation)+"}";
}
void SalesActions::finish() {
    if (!pending_ || !accepted_ || !observed_) return;
    status_=pending_->undo?L"완료 표시 취소 확인됨":L"판매 완료 확인됨";
    if(pending_->undo)++metrics_.undone;else ++metrics_.completed;
    pending_.reset(); accepted_=observed_=false;
}
void SalesActions::observe(const Json& snapshot) {
    std::lock_guard lock(mutex_);offer_.reset();
    auto* sales=field(snapshot.root(),"sales");
    auto* actions=yyjson_is_obj(sales)?field(sales,"actions"):nullptr;
    if (!yyjson_is_obj(actions)) return;
    const std::string generation(textField(actions,"generation"));
    auto* targets=field(actions,"targets");
    if (generation.empty() || generation.size()>128 || !yyjson_is_arr(targets) || yyjson_arr_size(targets)>30) return;
    auto* currentValue=field(sales,"currentMessageId");
    const auto current=yyjson_is_str(currentValue)?stringValue(currentValue):std::string_view{};
    std::size_t i=0,n=0;yyjson_val* target=nullptr;
    std::optional<SalesCommand> undo,complete,currentComplete;
    yyjson_arr_foreach(targets,i,n,target) {
        const std::string id(textField(target,"messageId"));if(!identifier(id))continue;
        if(pending_ && pending_->message==id) {
            auto* bot=field(target,"botCompleted");
            if(yyjson_is_bool(bot)) observed_=yyjson_is_true(bot)!=pending_->undo;
        }
        if(yyjson_is_true(field(target,"canComplete"))) {
            if(!complete)complete=SalesCommand{id,generation,{},false};
            if(id==current)currentComplete=SalesCommand{id,generation,{},false};
        }
        if(!undo && yyjson_is_true(field(target,"canUndo")))undo=SalesCommand{id,generation,{},true};
    }
    offer_=currentComplete?currentComplete:undo?undo:complete;
    finish();
}
void SalesActions::disconnected() {
    std::lock_guard lock(mutex_);offer_.reset();
    // Discard an unsent intent when the stream loses its authoritative state.
    if(queued_) {queued_.reset();pending_.reset();accepted_=observed_=false;status_=L"연결 변경으로 전송 취소됨";}
}
std::optional<SalesCommand> SalesActions::offered() const {std::lock_guard lock(mutex_);return pending_?std::nullopt:offer_;}
bool SalesActions::submit(const SalesCommand& offer) {
    std::lock_guard lock(mutex_);
    if(pending_ || !offer_ || !same(*offer_,offer))return false;
    pending_=offer;pending_->requestId=newRequestId();queued_=pending_;accepted_=observed_=false;
    ++metrics_.submitted;
    status_=L"판매 상태 전송 중";changed_.notify_one();return true;
}
std::optional<SalesCommand> SalesActions::wait(std::stop_token stop) {
    std::unique_lock lock(mutex_);changed_.wait(lock,stop,[&]{return queued_.has_value();});
    if(stop.stop_requested())return std::nullopt;
    auto command=std::move(queued_);queued_.reset();return command;
}
void SalesActions::response(const SalesCommand& command,unsigned httpStatus,std::string_view body) {
    std::lock_guard lock(mutex_);if(!pending_ || pending_->requestId!=command.requestId)return;
    if(httpStatus==200) {
        Json response(body,4096);
        if(numberField(response.root(),"protocolVersion")!=1 || textField(response.root(),"clientRequestId")!=command.requestId)throw std::runtime_error("Sales response mismatch");
        const auto disposition=textField(response.root(),"disposition");
        if(disposition=="accepted" || disposition=="noOp") {
            accepted_=true;status_=L"서버 판매 상태 확인 중";finish();return;
        }
        if(disposition!="rejectedUnauthorized" && disposition!="rejectedNotOwner" && disposition!="rejectedMessageMissing" && disposition!="rejectedInvalidState" &&
           disposition!="rejectedUnavailable" && disposition!="rejectedRateLimited" && disposition!="rejectedStale")
            throw std::runtime_error("Sales outcome uncertain");
        status_=disposition=="rejectedStale"?L"판매 정보가 변경되었습니다. 최신 상태에서 다시 확인해 주세요":L"판매 변경 거절됨 · 본인 글과 권한을 확인해 주세요";
    } else if(httpStatus==401 || httpStatus==403 || httpStatus==429)status_=L"판매 변경 거절됨 · 인증·권한 또는 요청 한도를 확인해 주세요";
    else throw std::runtime_error("Sales outcome uncertain");
    ++metrics_.rejected;pending_.reset();accepted_=observed_=false;
}
void SalesActions::uncertain(const SalesCommand& command) {
    std::lock_guard lock(mutex_);if(!pending_ || pending_->requestId!=command.requestId)return;
    // A lost response can follow a successful Discord write. Do not send again.
    ++metrics_.uncertain;
    accepted_=true;status_=L"판매 결과 미확인 · 자동 재전송 없이 서버 상태를 기다립니다";finish();
}
bool SalesActions::busy() const {std::lock_guard lock(mutex_);return pending_.has_value();}
std::wstring SalesActions::status() const {std::lock_guard lock(mutex_);return status_;}
SalesActionMetrics SalesActions::metrics() const {std::lock_guard lock(mutex_);return metrics_;}
void runSalesActions(SalesActions& actions,std::string_view token,std::stop_token stop,const std::function<void()>& notify) {
    while(auto command=actions.wait(stop)) {
        try {
            HttpTransport http(Endpoint(L"https://overlay.revo32.cloud"));Secret bearer("Bearer ",token);
            auto response=http.request(L"POST",L"/api/v1/sales/status",salesCommandBody(*command),bearer.view(),stop);
            actions.response(*command,response.status,response.body);
        } catch(const std::exception&) {actions.uncertain(*command);}
        if(notify)notify();
    }
}
}
